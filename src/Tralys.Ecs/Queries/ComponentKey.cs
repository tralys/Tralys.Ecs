namespace Tralys.Ecs;

/// <summary>Names a component type. Its dense index is known once a registry containing the type is built.</summary>
internal readonly unsafe struct ComponentKey : IEquatable<ComponentKey>
{
    readonly delegate* <int> index;

    ComponentKey(delegate* <int> index, Type type)
    {
        this.index = index;
        Type = type;
    }

    /// <summary>The key naming <typeparamref name="T"/>.</summary>
    public static ComponentKey Of<T>()
        where T : struct, IDeclared => new(&ComponentOpsOf<T>.GetIndex, typeof(T));

    /// <summary>The dense index, or -1 while no registry containing the type has been built.</summary>
    public int Index => index == null ? -1 : index();

    /// <summary>The type the key names.</summary>
    public Type Type { get; }

    /// <summary>Whether both keys name the same type.</summary>
    public bool Equals(ComponentKey other) => Type == other.Type;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ComponentKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Type?.GetHashCode() ?? 0;

    /// <summary>Whether both keys name the same type.</summary>
    public static bool operator ==(ComponentKey left, ComponentKey right) => left.Equals(right);

    /// <summary>Whether the keys name different types.</summary>
    public static bool operator !=(ComponentKey left, ComponentKey right) => !left.Equals(right);
}
