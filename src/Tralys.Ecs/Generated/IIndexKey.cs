namespace Tralys.Ecs.Generated;

/// <summary>Computes one index's key from a component, or returns false to leave the entity out of the index; the generator emits an
/// implementation per <see cref="IndexAttribute"/>.</summary>
public interface IIndexKey<TComponent, TKey>
    where TComponent : struct
    where TKey : notnull
{
    /// <summary>The key of <paramref name="value"/>, or false to leave the entity out; <paramref name="parent"/> is the entity's parent for a
    /// per-parent index and default otherwise.</summary>
    [Effect(Effects.None)]
    static abstract bool TryOf(in TComponent value, Entity parent, out TKey key);
}
