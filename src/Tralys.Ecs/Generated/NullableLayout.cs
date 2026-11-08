namespace Tralys.Ecs.Generated;

/// <summary>Mirrors <see cref="Nullable{T}"/>'s layout so generated metadata can address the value inside an <c>Entity?</c>.</summary>
public struct NullableLayout<T>
    where T : struct
{
    /// <summary>At the offset of <see cref="Nullable{T}"/>'s has-value flag.</summary>
    public bool HasValue;

    /// <summary>At the offset of <see cref="Nullable{T}"/>'s value; read only when <see cref="HasValue"/> is true.</summary>
    public T Value;
}
