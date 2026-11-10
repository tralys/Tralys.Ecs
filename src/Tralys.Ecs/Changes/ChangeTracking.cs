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

/// <summary>What a cursor's samples pass. <paramref name="Categories"/> and <paramref name="Components"/> filter <see cref="ChangeSample.Log"/>
/// by type; the entity filters also apply to <see cref="ChangeSample.Changed{T}"/> and <see cref="ChangeSample.ChangedRows{TKey, TRow}"/>,
/// and entries about the entity itself always pass.</summary>
/// <param name="Categories">Passes entries for types in any of these categories; <see cref="CategoryMask.All"/> passes every type.</param>
/// <param name="Components">Passes only entries whose <see cref="LogEntry.TypeIndex"/> is listed; null or empty passes every type.</param>
public readonly record struct CursorOptions(CategoryMask Categories, int[]? Components = null)
{
    /// <summary>Also pass an entity that is pending destroy when the sample is read, as <see cref="IncludePendingDestroyAttribute"/> does
    /// for a query; by default its typed entries and changed values are skipped.</summary>
    public bool IncludePendingDestroy { get; init; }

    /// <summary>Also pass an entity that is detached when the sample is read, as <see cref="IncludeDetachedAttribute"/> does for a query;
    /// by default its typed entries and changed values are skipped.</summary>
    public bool IncludeDetached { get; init; }
}

/// <summary>Whether a sample's log is complete.</summary>
public enum CursorStatus
{
    /// <summary>The log holds every entry since the cursor's last commit.</summary>
    Ok,

    /// <summary>The log was trimmed past this cursor; the consumer must resync from current state.</summary>
    Expired,
}

/// <summary>Per-consumer change cursors over per-row stamps and the structural log.</summary>
public sealed class ChangeTracking
{
    readonly World world;
    internal readonly StructuralLog log = new();
    readonly int retentionCap;
    readonly AttributedLog? attributed;
    Cursor[] cursors = new Cursor[4];
    int live;

    // Live cursors, plus one while an attributed log listens.
    int sinks;

    internal ChangeTracking(World world, int retentionCap, AttributedLog? attributed)
    {
        this.world = world;
        this.retentionCap = retentionCap;
        this.attributed = attributed;
        sinks = attributed is null ? 0 : 1;
    }

    internal struct Cursor
    {
        public bool Live;
        public int Generation;
        public string Name;
        public ulong Since;
        public CursorOptions Options;
    }

    internal bool LogActive => live > 0;

    internal bool Logging => sinks > 0;

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
        live++;
        sinks++;
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
        ref var c = ref Get(cursor);
        c.Live = false;
        live--;
        sinks--;
        Trim();
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
        var status = c.Since < log.TrimmedThrough ? CursorStatus.Expired : CursorStatus.Ok;
        return new ChangeSample(world, c.Since, until, status, c.Options);
    }

    /// <summary>Marks everything up to <paramref name="upTo"/> consumed; the log is trimmed at the slowest live cursor.</summary>
    /// <exception cref="ArgumentException">The cursor is not registered.</exception>
    [Effect(Effects.Mutates)]
    public void Commit(CursorId cursor, ulong upTo)
    {
        world.CheckWritable();
        ref var c = ref Get(cursor);
        if (upTo > c.Since)
            c.Since = upTo;
        Trim();
    }

    /// <summary>The name the cursor was registered with.</summary>
    /// <exception cref="ArgumentException">The cursor is not registered.</exception>
    [Effect(Effects.None)]
    public string NameOf(CursorId cursor) => Get(cursor).Name;

    /// <summary>The world version of the last stamped write to the entity's <typeparamref name="T"/>; 0 when it lacks one, holds it disabled, does not exist, or <typeparamref name="T"/> is <see cref="UntrackedAttribute"/>.</summary>
    [Effect(Effects.None)]
    public ulong VersionOf<T>(Entity entity)
        where T : struct, IComponent => world.StampOf<T>(entity, added: false);

    /// <summary>The world version at which the entity gained its <typeparamref name="T"/>; 0 when it lacks one, holds it disabled, does not exist, or <typeparamref name="T"/> is <see cref="UntrackedAttribute"/>.</summary>
    [Effect(Effects.None)]
    public ulong AddedVersionOf<T>(Entity entity)
        where T : struct, IComponent => world.StampOf<T>(entity, added: true);

    /// <summary>The newest version at which anything below <paramref name="root"/> changed: a write to a <see cref="PropagateChangesAttribute"/>
    /// component, a child gained, lost or reordered, a descendant marked or spared pending destroy, detached or reattached, or an inherited
    /// tag (<see cref="TagAttribute.Inherited"/>) added, removed, enabled or disabled on an ancestor so that it takes or loses effect on a
    /// descendant, each as it applies. A descendant's own write of that tag counts only when the tag is declared
    /// <see cref="PropagateChangesAttribute"/>.</summary>
    [Effect(Effects.None)]
    public ulong DescendantVersionOf(Entity root)
    {
        ref var node = ref world.hierarchy.Find(root);
        return Unsafe.IsNullRef(ref node) ? 0 : Volatile.Read(ref node.DescendantVersion);
    }

    /// <summary>The newest version written to a <see cref="PropagateChangesAttribute"/> component on any target of
    /// <paramref name="source"/>, over every relation, or at which its <typeparamref name="R"/> edges last changed.</summary>
    [Effect(Effects.None)]
    public ulong RelatedVersionOf<R>(Entity source)
        where R : struct, IRelation => world.RelatedVersionOf<R>(source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(
        LogKind kind,
        Entity entity,
        int type,
        LifecycleReason reason,
        ulong detail = 0
    )
    {
        if (sinks != 0)
            AppendLogged(kind, entity, type, reason, detail);
    }

    void AppendLogged(LogKind kind, Entity entity, int type, LifecycleReason reason, ulong detail)
    {
        var entry = new LogEntry(kind, entity, world.version, type, reason, detail);
        attributed?.Add(entry, world.currentSystem);
        if (live == 0)
            return;
        log.Append(entry);
        if (log.EnforceCap(retentionCap))
            TrimJournals(log.TrimmedThrough);
    }

    void Trim()
    {
        if (live == 0)
        {
            log.Clear();
            TrimJournals(ulong.MaxValue);
            return;
        }
        var min = ulong.MaxValue;
        foreach (var c in cursors)
        {
            if (c.Live)
                min = Math.Min(min, c.Since);
        }
        log.DropThrough(min);
        TrimJournals(min);
    }

    internal LogStats Stats()
    {
        int registered = 0,
            expired = 0;
        CursorId? oldest = null;
        var oldestSince = ulong.MaxValue;
        for (var i = 0; i < cursors.Length; i++)
        {
            ref readonly var c = ref cursors[i];
            if (!c.Live)
                continue;
            registered++;
            if (c.Since < log.TrimmedThrough)
                expired++;
            if (c.Since < oldestSince)
            {
                oldestSince = c.Since;
                oldest = new CursorId(i, c.Generation);
            }
        }
        return new LogStats(
            log.Count,
            registered,
            expired,
            oldest,
            oldest is null ? 0 : oldestSince
        );
    }

    internal readonly List<IKeyJournal> journals = [];

    void TrimJournals(ulong through)
    {
        foreach (var journal in journals)
            journal.DropThrough(through);
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

    internal void Expire()
    {
        log.Clear();
        log.TrimmedThrough = world.version;
        TrimJournals(ulong.MaxValue);
        for (var i = 0; i < cursors.Length; i++)
        {
            if (cursors[i].Live)
                cursors[i].Since = 0;
        }
    }

    internal void Reset()
    {
        log.Clear();
        TrimJournals(ulong.MaxValue);
        for (var i = 0; i < cursors.Length; i++)
        {
            if (cursors[i].Live)
                cursors[i].Since = world.version;
        }
        world.version++;
    }
}
