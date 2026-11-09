using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    internal readonly Entity[] singletonHolders;
    Entity singletonOwner;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClaimSingleton(Entity entity, int type)
    {
        var slot = layout.SingletonSlot[type];
        if (slot >= 0)
            ClaimSingleton(entity, type, slot);
    }

    void ClaimSingleton(Entity entity, int type, int slot)
    {
        if (SingletonRival(entity, slot) is { IsDefault: false } holder)
            throw new InvalidOperationException(SingletonConflict(type, holder, entity.ToString()));
        singletonHolders[slot] = entity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void HoldSingleton(Entity entity, int type)
    {
        var slot = layout.SingletonSlot[type];
        if (slot >= 0)
            singletonHolders[slot] = entity;
    }

    Entity SingletonRival(Entity claimant, int slot)
    {
        var holder = singletonHolders[slot];
        return holder != claimant && allocator.IsCurrent(holder) ? holder : default;
    }

    string SingletonConflict(int type, Entity holder, string claimant) =>
        $"{registry[type].Name} is a singleton already held by {holder}; {claimant} cannot hold a second.";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ReleaseSingleton(Entity entity, int type)
    {
        var slot = layout.SingletonSlot[type];
        if (slot >= 0 && singletonHolders[slot] == entity)
            singletonHolders[slot] = default;
    }

    Entity SingletonHolder<T>()
        where T : struct, ISingleton
    {
        var slot = layout.SingletonSlot[registry.IndexOf<T>()];
        var holder = singletonHolders[slot];
        return allocator.IsCurrent(holder) ? holder : default;
    }

    /// <summary>Some entity holds the singleton <typeparamref name="T"/> present, one pending destroy included; a disabled one reads as
    /// absent, as <see cref="Has{T}"/> does, yet still keeps any other entity from taking <typeparamref name="T"/>.</summary>
    [Effect(Effects.None)]
    public bool HasSingleton<T>()
        where T : struct, ISingleton => Has<T>(SingletonEntity<T>());

    /// <summary>The entity holding the singleton <typeparamref name="T"/>, enabled or disabled, or null when none does.</summary>
    [Effect(Effects.None)]
    public Entity SingletonEntity<T>()
        where T : struct, ISingleton
    {
        return SingletonHolder<T>();
    }

    /// <summary>The singleton by <c>ref readonly</c> from its holder; the ref is valid until the next structural change.</summary>
    /// <exception cref="InvalidOperationException">No entity holds <typeparamref name="T"/>, or its holder has it disabled.</exception>
    [Effect(Effects.Borrows)]
    public ref readonly T GetSingleton<T>()
        where T : struct, ISingleton
    {
        var holder = SingletonHolder<T>();
        if (holder.IsDefault)
            throw new InvalidOperationException($"No entity holds the singleton {typeof(T).Name}.");
        return ref Get<T>(holder);
    }

    /// <summary>Copies the singleton out of its holder; false when no entity holds it or its holder has it disabled.</summary>
    [Effect(Effects.None)]
    public bool TryGetSingleton<T>(out T value)
        where T : struct, ISingleton
    {
        var holder = SingletonHolder<T>();
        if (holder.IsDefault)
        {
            value = default;
            return false;
        }
        return TryGet(holder, out value);
    }

    /// <summary>A scoped edit of the singleton on its holder, written in place at once; disposing it stamps only if the value
    /// changed.</summary>
    /// <exception cref="InvalidOperationException">No entity holds <typeparamref name="T"/>, or its holder has it disabled.</exception>
    [Effect(Effects.Mutates)]
    public ComponentEdit<T> EditSingleton<T>()
        where T : struct, ISingleton, IMutableComponent
    {
        var holder = SingletonHolder<T>();
        if (holder.IsDefault)
            throw new InvalidOperationException($"No entity holds the singleton {typeof(T).Name}.");
        return Edit<T>(holder);
    }

    /// <summary>Sets a singleton on its current holder, enabling a disabled one, or on the world-owned singleton entity.</summary>
    /// <exception cref="InvalidOperationException">A serial query body is running and the set would create the owner, or add or enable
    /// the singleton.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public void SetSingleton<T>(
        in T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, ISingleton
    {
        At(file, line);
        var holder = SingletonHolder<T>();
        if (holder.IsDefault)
        {
            if (!allocator.IsCurrent(singletonOwner))
            {
                CheckWritable();
                singletonOwner = NewEntity();
            }
            holder = singletonOwner;
        }
        Set(holder, value, file: file, line: line);
    }
}
