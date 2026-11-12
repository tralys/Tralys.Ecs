using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>A query term reading <typeparamref name="T"/> on the entity's parent. <see cref="Entity"/> is the parent, null at a root;
/// <see cref="HasValue"/> is false when the parent lacks <typeparamref name="T"/> or holds it disabled.</summary>
public readonly ref struct Parent<T>
    where T : struct, IComponent
{
    readonly ref readonly T value;

    internal Parent(Entity parent, ref readonly T value)
    {
        Entity = parent;
        this.value = ref value;
    }

    /// <summary>The parent, or null at a root.</summary>
    public Entity Entity { get; }

    /// <summary>The entity has a parent and the parent has <typeparamref name="T"/>.</summary>
    public bool HasValue => !Unsafe.IsNullRef(in value);

    /// <summary>The parent's <typeparamref name="T"/> by <c>ref readonly</c>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="HasValue"/> is false.</exception>
    public ref readonly T Value
    {
        get
        {
            if (Unsafe.IsNullRef(in value))
                Optional<T>.ThrowEmpty();
            return ref value;
        }
    }
}

/// <summary>A query term reading <typeparamref name="T"/> on the nearest ancestor holding it; <see cref="Entity"/> is that ancestor.</summary>
public readonly ref struct Up<T>
    where T : struct, IComponent
{
    readonly ref readonly T value;

    internal Up(Entity ancestor, ref readonly T value)
    {
        Entity = ancestor;
        this.value = ref value;
    }

    /// <summary>The nearest strict ancestor holding <typeparamref name="T"/>, or null when none does.</summary>
    public Entity Entity { get; }

    /// <summary>Some strict ancestor holds <typeparamref name="T"/>.</summary>
    public bool HasValue => !Unsafe.IsNullRef(in value);

    /// <summary>That ancestor's <typeparamref name="T"/> by <c>ref readonly</c>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="HasValue"/> is false.</exception>
    public ref readonly T Value
    {
        get
        {
            if (Unsafe.IsNullRef(in value))
                Optional<T>.ThrowEmpty();
            return ref value;
        }
    }
}

public sealed partial class World
{
    internal Parent<T> ParentTerm<T>(Entity entity)
        where T : struct, IComponent
    {
        var parent = hierarchy.ParentOf(entity);
        if (parent.IsDefault)
            return default;
        return new Parent<T>(parent, in Read<T>(parent));
    }

    /// <summary>A copy of <paramref name="root"/> and every descendant, parents before children, in child order.</summary>
    [Effect(Effects.OwnerThread)]
    public ScratchBuffer<Entity> SnapshotSubtree(Entity root)
    {
        var count = 0;
        var walked = 0;
        Push(ref count, root);
        while (walked < count)
        {
            foreach (var child in hierarchy.ChildrenOf(walk[walked++]))
                Push(ref count, child);
        }
        var copy = Scratch.Rent<Entity>(count);
        walk.AsSpan(0, count).CopyTo(copy.Span);
        return copy;
    }

    /// <summary>A copy of <paramref name="parent"/>'s children in order, safe to walk while the hierarchy changes.</summary>
    [Effect(Effects.OwnerThread)]
    public ScratchBuffer<Entity> SnapshotChildren(Entity parent)
    {
        return ScratchCopy(hierarchy.ChildrenOf(parent));
    }

    /// <summary>A copy of the entities with an <typeparamref name="R"/> edge to <paramref name="target"/>, in the order they were related.</summary>
    [Effect(Effects.OwnerThread)]
    public ScratchBuffer<Entity> SnapshotSources<R>(Entity target)
        where R : struct, IRelation
    {
        return ScratchCopy(FindRelations<R>() is { } store ? store.SourcesOf(target) : default);
    }

    /// <summary>A copy of <paramref name="source"/>'s <typeparamref name="R"/> targets, in the order they were related.</summary>
    [Effect(Effects.OwnerThread)]
    public ScratchBuffer<Entity> SnapshotTargets<R>(Entity source)
        where R : struct, IRelation
    {
        var targets = Targets<R>(source);
        var copy = Scratch.Rent<Entity>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
            copy.Span[i] = targets[i];
        return copy;
    }

    ScratchBuffer<Entity> ScratchCopy(ReadOnlySpan<Entity> entities)
    {
        var copy = Scratch.Rent<Entity>(entities.Length);
        entities.CopyTo(copy.Span);
        return copy;
    }
}
