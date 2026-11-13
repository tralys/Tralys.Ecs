namespace Tralys.Ecs;

/// <summary>Reads the value indexes of <typeparamref name="T"/>. As a system field it declares a read of <typeparamref name="T"/>; the
/// generator adds one accessor per index, such as <c>lookup.ById()</c>.</summary>
public readonly struct IndexLookup<T>(World world)
    where T : struct, IComponent
{
    internal World World => world;
}
