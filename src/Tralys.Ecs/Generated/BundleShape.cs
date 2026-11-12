namespace Tralys.Ecs.Generated;

/// <summary>Collects a bundle's component types; the generated <c>IBundle.Describe</c> calls <see cref="Add{T}"/> once per field.</summary>
public ref struct BundleShape
{
    readonly List<int> types;

    internal BundleShape(List<int> types) => this.types = types;

    /// <summary>Adds <typeparamref name="T"/> to the bundle's types.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is in no registry built so far.</exception>
    public readonly void Add<T>()
        where T : struct, IComponent
    {
        var index = ComponentType<T>.Index;
        if (index < 0)
            throw new InvalidOperationException($"{typeof(T).Name} is not registered.");
        types.Add(index);
    }
}
