using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>A registered change cursor: a slot and the slot's generation, so an id kept after
/// <see cref="ChangeTracking.Unregister"/> never addresses a cursor registered later in the same slot.</summary>
public readonly record struct CursorId
{
    internal CursorId(int slot, int generation)
    {
        Slot = slot;
        Generation = generation;
    }

    internal int Slot { get; }
    internal int Generation { get; }

    /// <inheritdoc/>
    public override string ToString() => $"Cursor({Slot}v{Generation})";
}

/// <summary>Which entities a cursor's <see cref="ChangeSample.Changed{T}"/> and <see cref="ChangeSample.ChangedRows{TKey, TRow}"/>
/// pass.</summary>
public readonly record struct CursorOptions
{
    /// <summary>Also pass an entity that is pending destroy when the sample is read, as <see cref="IncludePendingDestroyAttribute"/> does
    /// for a query; by default its changed values are skipped.</summary>
    public bool IncludePendingDestroy { get; init; }

    /// <summary>Also pass an entity that is detached when the sample is read, as <see cref="IncludeDetachedAttribute"/> does for a query;
    /// by default its changed values are skipped.</summary>
    public bool IncludeDetached { get; init; }
}

/// <summary>Per-consumer change cursors over per-row stamps, and the watches that also hear structural changes.</summary>
public sealed class ChangeTracking
{
    readonly World world;
    readonly int queueCap;
    readonly AttributedLog? attributed;
    Cursor[] cursors = new Cursor[4];
    WatchQueue[] queues = [];
    WatchQueue[] raisers = [];
    LogKinds[] wanted = [];
    LogKinds wantedEntity;
    LogKinds wantedAny;
    internal long structuralChanges;
    internal ulong Epoch { get; private set; }

    internal ChangeTracking(World world, int queueCap, AttributedLog? attributed)
    {
        this.world = world;
        this.queueCap = queueCap;
        this.attributed = attributed;
    }

    internal struct Cursor
    {
        public bool Live;
        public int Generation;
        public string Name;
        public ulong Since;
        public CursorOptions Options;
    }

    /// <summary>Registers a cursor that sees changes made from now on.</summary>
    [Effect(Effects.Mutates)]
    public CursorId Register(string name, in CursorOptions options = default)
    {
        world.CheckWritable();
        var index = Array.FindIndex(cursors, c => !c.Live);
        if (index < 0)
        {
            index = cursors.Length;
            Array.Resize(ref cursors, cursors.Length * 2);
        }
        var generation = cursors[index].Generation + 1;
        cursors[index] = new Cursor
        {
            Live = true,
            Generation = generation,
            Name = name,
            Since = world.version,
            Options = options,
        };
        world.version++;
        return new CursorId(index, generation);
    }

    /// <summary>Registers a cursor that marks what <paramref name="declare"/>'s inputs change from now on; <typeparamref name="TScope"/>
    /// says what is owed when changes cannot be followed one by one.</summary>
    [Effect(Effects.Mutates)]
    public ChangeWatch<TBits> Watch<TBits, TScope>(string name, Action<WatchBuilder<TBits>> declare)
        where TBits : unmanaged, Enum
        where TScope : IWatchScope<TBits>
    {
        var builder = new WatchBuilder<TBits>(world.registry);
        declare(builder);
        return new ChangeWatch<TBits>(world, name, builder, TScope.Everything, TScope.Whole);
    }

    /// <summary>Drops the cursor; the id is dead from then on.</summary>
    /// <exception cref="ArgumentException">The cursor is not registered.</exception>
    [Effect(Effects.Mutates)]
    public void Unregister(CursorId cursor)
    {
        world.CheckWritable();
        Get(cursor).Live = false;
    }

    /// <summary>Samples changes since the cursor's last commit, up to the current version, and advances the version.</summary>
    /// <exception cref="ArgumentException">The cursor is not registered.</exception>
    [Effect(Effects.Mutates)]
    public ChangeSample Sample(CursorId cursor)
    {
        world.CheckWritable();
        ref var c = ref Get(cursor);
        var until = world.version;
        world.version++;
        return new ChangeSample(world, c.Since, until, c.Options, Epoch);
    }

    /// <summary>Marks the sample <paramref name="window"/> came from consumed, so the cursor's next sample starts after it. A window
    /// sampled before the world was last reset or cleared is ignored, since the cursor started over then.</summary>
    /// <exception cref="ArgumentException">The cursor is not registered.</exception>
    [Effect(Effects.Mutates)]
    public void Commit(CursorId cursor, ChangeWindow window)
    {
        world.CheckWritable();
        ref var c = ref Get(cursor);
        if (window.Epoch == Epoch && window.Until > c.Since)
            c.Since = window.Until;
    }

    /// <summary>The name the cursor was registered with.</summary>
    /// <exception cref="ArgumentException">The cursor is not registered.</exception>
    [Effect(Effects.None)]
    public string NameOf(CursorId cursor) => Get(cursor).Name;

    /// <summary>The world version of the last stamped write to the entity's <typeparamref name="T"/>; 0 when it lacks one, holds it disabled, or does not exist.</summary>
    [Effect(Effects.None)]
    public ulong VersionOf<T>(Entity entity)
        where T : struct, IComponent, ITracked => world.StampOf<T>(entity, added: false);

    /// <summary>The world version at which the entity gained its <typeparamref name="T"/>; 0 when it lacks one, holds it disabled, or does not exist.</summary>
    [Effect(Effects.None)]
    public ulong AddedVersionOf<T>(Entity entity)
        where T : struct, IComponent, ITracked => world.StampOf<T>(entity, added: true);

    /// <summary>The world version at which <paramref name="owner"/>'s <typeparamref name="TRow"/> row keyed <paramref name="key"/> was
    /// added or last written; 0 when there is none.</summary>
    [Effect(Effects.None)]
    public ulong VersionOf<TKey, TRow>(Entity owner, in TKey key)
        where TKey : notnull
        where TRow : struct, IRow<TKey>, ITracked =>
        world.FindRows<TKey, TRow>() is { } store
        && store.BlockOf(owner) is { } block
        && store.Find(block, key) is var at and >= 0
            ? block.Changed[at]
            : 0;

    /// <summary>The newest version at which anything below <paramref name="root"/> changed: a write to or removal of a
    /// <see cref="PropagateChangesAttribute"/> component, a child gained, lost or reordered, a descendant marked or spared pending destroy, detached or reattached, or an inherited
    /// tag (<see cref="TagAttribute.Inherited"/>) added, removed, enabled or disabled on an ancestor so that it takes or loses effect on a
    /// descendant, each as it applies. A descendant's own write of that tag counts only when the tag is declared
    /// <see cref="PropagateChangesAttribute"/>.</summary>
    [Effect(Effects.None)]
    public ulong DescendantVersionOf(Entity root)
    {
        ref var node = ref world.hierarchy.Find(root);
        return Unsafe.IsNullRef(ref node) ? 0 : Volatile.Read(ref node.DescendantVersion);
    }

    /// <summary>The newest version written to or removed from a <see cref="PropagateChangesAttribute"/> component on any target of
    /// <paramref name="source"/>, over every relation, or at which its <typeparamref name="R"/> edges last changed.</summary>
    [Effect(Effects.None)]
    public ulong RelatedVersionOf<R>(Entity source)
        where R : struct, IRelation => world.RelatedVersionOf<R>(source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Wants(int type, LogKind kind) =>
        attributed is not null
        || (
            type < 0
                ? wantedEntity.Has(kind)
                : (uint)type < (uint)wanted.Length && wanted[type].Has(kind)
        );

    internal bool Wants(int type, LogKinds kinds) =>
        attributed is not null
        || ((uint)type < (uint)wanted.Length && wanted[type].Overlaps(kinds));

    internal bool WantsAny(LogKinds kinds) => attributed is not null || wantedAny.Overlaps(kinds);

    internal bool TakesRaises => raisers.Length > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(
        LogKind kind,
        Entity entity,
        int type,
        LifecycleReason reason,
        ulong detail = 0
    )
    {
        if (kind != LogKind.Replaced)
            structuralChanges++;
        if (Wants(type, kind))
            AppendWanted(kind, entity, type, reason, detail);
    }

    void AppendWanted(LogKind kind, Entity entity, int type, LifecycleReason reason, ulong detail)
    {
        attributed?.Add(
            new LogEntry(kind, entity, world.version, type, reason, detail),
            world.currentSystem
        );
        foreach (var queue in queues)
        {
            if (queue.Wants(type, kind))
                queue.Push(new WatchEvent(entity, type, kind));
        }
    }

    // Only an ancestor's first raise since the watch's commit finds it at or below Since.
    internal void Raised(Entity ancestor, ulong prior)
    {
        foreach (var queue in raisers)
        {
            if (prior <= cursors[queue.Slot].Since)
                queue.PushRaised(ancestor);
        }
    }

    internal WatchQueue Subscribe(CursorId cursor, LogKinds[] kinds, bool takesRaises)
    {
        var queue = new WatchQueue(cursor.Slot, kinds, takesRaises, queueCap);
        queues = [.. queues, queue];
        if (takesRaises)
            raisers = [.. raisers, queue];
        Recompute();
        return queue;
    }

    internal void Unsubscribe(WatchQueue queue)
    {
        queues = Array.FindAll(queues, q => q != queue);
        raisers = Array.FindAll(raisers, q => q != queue);
        Recompute();
    }

    void Recompute()
    {
        var length = 0;
        foreach (var queue in queues)
            length = Math.Max(length, queue.Kinds.Length);
        wanted = new LogKinds[length];
        wantedAny = default;
        foreach (var queue in queues)
        {
            for (var type = 0; type < queue.Kinds.Length; type++)
            {
                wanted[type] |= queue.Kinds[type];
                wantedAny |= queue.Kinds[type];
            }
        }
        wantedEntity = queues.Length > 0 ? WatchQueue.EntityKinds : default;
    }

    ref Cursor Get(CursorId cursor)
    {
        if ((uint)cursor.Slot < (uint)cursors.Length)
        {
            ref var found = ref cursors[cursor.Slot];
            if (found.Live && found.Generation == cursor.Generation)
                return ref found;
        }
        throw new ArgumentException($"{cursor} is not registered.", nameof(cursor));
    }

    internal void BeginEpoch() => Epoch++;

    internal void Expire()
    {
        for (var i = 0; i < cursors.Length; i++)
        {
            if (cursors[i].Live)
                cursors[i].Since = 0;
        }
        foreach (var queue in queues)
            queue.Overflow();
    }

    internal void Reset()
    {
        foreach (var queue in queues)
            queue.Clear();
        for (var i = 0; i < cursors.Length; i++)
        {
            if (cursors[i].Live)
                cursors[i].Since = world.version;
        }
        world.version++;
    }
}
