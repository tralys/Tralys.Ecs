using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>The descriptors one assembly declares; the generator emits one per assembly.</summary>
public sealed class ComponentModule(
    string assembly,
    ComponentDescriptor[] descriptors,
    bool optimized
)
{
    /// <summary>The name of the declaring assembly.</summary>
    public string Assembly { get; } = assembly;

    /// <summary>The descriptors the assembly declares.</summary>
    public IReadOnlyList<ComponentDescriptor> Descriptors { get; } = descriptors;

    /// <summary>Whether the declaring assembly was compiled without the <c>DEBUG</c> symbol; a registry with a module compiled with it turns
    /// <see cref="WorldOptions.CheckBorrows"/> on by default.</summary>
    public bool Optimized { get; } = optimized;
}

/// <summary>An immutable set of component modules. Dense indices are process-wide, so worlds from different registries agree on them.</summary>
public sealed class Registry
{
    static readonly Lock bindGate = new();
    static int nextIndex;

    readonly ComponentDescriptor?[] byIndex;
    readonly ComponentDescriptor[] sorted;
    readonly Dictionary<string, ComponentDescriptor> byName;

    Registry(ComponentDescriptor?[] byIndex, ComponentDescriptor[] sorted, bool optimized)
    {
        this.byIndex = byIndex;
        this.sorted = sorted;
        byName = sorted.ToDictionary(d => d.Name, StringComparer.Ordinal);
        Optimized = optimized;
    }

    /// <summary>A registry of every descriptor in <paramref name="modules"/>, counting a repeated descriptor once; throws <see cref="InvalidOperationException"/> for a type name two modules declare or a missing required type, <see cref="NotSupportedException"/> for a sparse enableable type.</summary>
    public static Registry Build(params ReadOnlySpan<ComponentModule> modules)
    {
        var seen = new HashSet<ComponentDescriptor>(ReferenceEqualityComparer.Instance);
        var byName = new Dictionary<string, ComponentDescriptor>(StringComparer.Ordinal);
        var optimized = true;
        foreach (var module in modules)
        {
            optimized &= module.Optimized;
            foreach (var descriptor in module.Descriptors)
            {
                if (!seen.Add(descriptor))
                    continue;
                if (!byName.TryAdd(descriptor.Name, descriptor))
                    throw new InvalidOperationException(
                        $"{descriptor.Name} is declared by two modules."
                    );
                Validate(descriptor);
            }
        }

        var sorted = byName.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToArray();
        var max = -1;
        lock (bindGate)
        {
            foreach (var descriptor in sorted)
            {
                var index = Bind(descriptor);
                descriptor.Index = index;
                max = Math.Max(max, index);
            }
        }

        var byIndex = new ComponentDescriptor?[max + 1];
        foreach (var descriptor in sorted)
            byIndex[descriptor.Index] = descriptor;

        foreach (var descriptor in sorted)
        {
            var required = descriptor.RequiresNames;
            var resolved = new int[required.Length];
            for (var i = 0; i < required.Length; i++)
            {
                if (!byName.TryGetValue(required[i], out var target))
                    throw new InvalidOperationException(
                        $"{descriptor.Name} requires {required[i]}, which no module in this registry declares."
                    );
                resolved[i] = target.Index;
            }
            descriptor.requires = resolved;
        }

        return new Registry(byIndex, sorted, optimized);
    }

    static unsafe int Bind(ComponentDescriptor descriptor)
    {
        var existing = descriptor.Ops.GetIndex();
        if (existing >= 0)
            return existing;
        var index = nextIndex++;
        descriptor.Ops.Bind(index);
        return index;
    }

    static void Validate(ComponentDescriptor descriptor)
    {
        if (descriptor.IsSparse && descriptor.IsEnableable)
            throw new NotSupportedException(
                $"{descriptor.Name}: sparse components and tags carry no enable bits."
            );
    }

    internal int IndexBound => byIndex.Length;

    internal bool Optimized { get; }

    /// <summary>Every descriptor, in ordinal name order.</summary>
    public ReadOnlySpan<ComponentDescriptor> Descriptors => sorted;

    /// <summary>Whether dense index <paramref name="index"/> names a type in this registry.</summary>
    public bool Contains(int index) => At(index) is not null;

    /// <summary>The descriptor at dense index <paramref name="index"/>; throws <see cref="ArgumentOutOfRangeException"/> when it is not in this registry.</summary>
    public ComponentDescriptor this[int index] =>
        At(index)
        ?? throw new ArgumentOutOfRangeException(
            nameof(index),
            $"Dense index {index} is not in this registry."
        );

    ComponentDescriptor? At(int index) =>
        (uint)index < (uint)byIndex.Length ? byIndex[index] : null;

    /// <summary>The descriptor named <paramref name="name"/>, or null.</summary>
    public ComponentDescriptor? FindByName(string name) => byName.GetValueOrDefault(name);

    internal RegistryLayout Layout => layout ??= new RegistryLayout(this);
    RegistryLayout? layout;
    QueryShape?[] shapes = [];
    readonly Lock shapeGate = new();

    internal QueryShape ShapeOf(QueryDesc desc)
    {
        lock (shapeGate)
        {
            if (desc.Id >= shapes.Length)
                Array.Resize(ref shapes, Math.Max(desc.Id + 1, shapes.Length * 2));
            return shapes[desc.Id] ??= new QueryShape(this, desc);
        }
    }

    /// <summary>The descriptor of <typeparamref name="T"/>; throws <see cref="InvalidOperationException"/> when it is not in this registry.</summary>
    public ComponentDescriptor Get<T>()
        where T : struct => this[IndexOf<T>()];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int IndexOf<T>()
        where T : struct
    {
        var index = ComponentType<T>.Index;
        if ((uint)index >= (uint)byIndex.Length || byIndex[index] is null)
            ThrowUnregistered<T>();
        return index;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowUnregistered<T>() =>
        throw new InvalidOperationException(
            $"{typeof(T).Name} is not in this world's registry; pass its assembly's module to Registry.Build."
        );
}
