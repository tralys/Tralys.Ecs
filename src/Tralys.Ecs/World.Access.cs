using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal enum LocationKind : byte
{
    None,
    Column,
    Tag,
    Sparse,
    Overlay,
}

internal struct ValueLocation
{
    public LocationKind Kind;
    public Archetype? Archetype;
    public int Row;
    public int Tracked;
    public SparseStorage? Sparse;
    public bool Disabled;
    public bool FlipRecorded;

    public readonly bool IsTracked =>
        (Kind == LocationKind.Column && Tracked >= 0)
        || (Kind == LocationKind.Sparse && Tracked >= 0);
}

public sealed partial class World
{
    internal readonly CommandBuffer stage;
    internal readonly PendingOverlay overlay;
    internal readonly ChangeTracking changes;
    internal readonly ObserverRegistry observers;

    /// <summary>Per-consumer change cursors over this world's row stamps, and watches over its structural changes.</summary>
    public ChangeTracking Changes => changes;

    /// <summary>This world's read-only view, as a query body's <see cref="WorldReader"/> parameter sees it.</summary>
    public WorldReader Reader => new(this);

    /// <summary>This world's lifecycle observers; every event reaches them at the next playback point, in operation order.</summary>
    public ObserverRegistry Observers => observers;

    // Empty on purpose: a member that calls it is marked as changing structure.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Effect(Effects.Structural)]
    static void Structural() { }

    /// <summary>The entity has <typeparamref name="T"/>, counting pending changes; a disabled <see cref="EnableableAttribute"/> one reads
    /// as absent, as in queries. False when the entity does not exist.</summary>
    [Effect(Effects.None)]
    public bool Has<T>(Entity entity)
        where T : struct, IComponent
    {
        ref var slot = ref allocator.Current(entity);
        return !Unsafe.IsNullRef(ref slot) && Holds(slot, entity, registry.IndexOf<T>(), false);
    }

    /// <summary>The entity stores <typeparamref name="T"/>, enabled or disabled, counting pending changes; false when it does not exist.
    /// With <see cref="TryGetStored{T}"/>, the only read that sees a disabled component.</summary>
    [Effect(Effects.None)]
    public bool HasStored<T>(Entity entity)
        where T : struct, IComponent
    {
        ref var slot = ref allocator.Current(entity);
        return !Unsafe.IsNullRef(ref slot) && Holds(slot, entity, registry.IndexOf<T>(), true);
    }

    internal bool IsPresent(Entity entity, int type) =>
        Holds(allocator.Slots[entity.Index], entity, type, false);

    internal bool IsStored(Entity entity, int type) =>
        Holds(allocator.Slots[entity.Index], entity, type, true);

    bool Holds(in EntitySlot slot, Entity entity, int type, bool disabledToo)
    {
        if (
            slot.Pending
            && overlay.TryGet(entity.Index, type, out var present, out var enabled, out _)
        )
            return disabledToo ? present : enabled;
        return disabledToo || !layout.Enableable[type]
            ? StoredIn(slot, entity, type)
            : slot.Archetype?.EnableSlot(type) is int enable and >= 0
                && slot.Archetype.IsEnabled(enable, slot.Row);
    }

    internal bool StoredIn(in EntitySlot slot, Entity entity, int type) =>
        layout.Class[type] switch
        {
            StorageClass.Column or StorageClass.SignatureTag => slot.Archetype?.Signature.Has(type)
                == true,
            StorageClass.Sparse => sparse[layout.SparseSlot[type]]?.Contains(entity.Index) == true,
            StorageClass.SparseTag => sparseTags[layout.SparseTagSlot[type]]?.Contains(entity.Index)
                == true,
            _ => false,
        };

    /// <summary>The component by <c>ref readonly</c>, pending adds included; the ref is valid until the next structural change.</summary>
    /// <exception cref="InvalidOperationException">The entity lacks <typeparamref name="T"/>, holds it disabled, or does not
    /// exist.</exception>
    [Effect(Effects.None)]
    public ref readonly T Get<T>(Entity entity)
        where T : struct, IComponent
    {
        ref var value = ref Read<T>(entity);
        if (Unsafe.IsNullRef(ref value))
            ThrowMissing<T>(entity);
        return ref value;
    }

    /// <summary>Copies the component out; false when the entity lacks it, holds it disabled, or does not exist.</summary>
    [Effect(Effects.None)]
    public bool TryGet<T>(Entity entity, out T value)
        where T : struct, IComponent
    {
        ref var found = ref Read<T>(entity);
        if (Unsafe.IsNullRef(ref found))
        {
            value = default;
            return false;
        }
        value = found;
        return true;
    }

    /// <summary>Copies the stored component out, enabled or disabled; false when the entity lacks it or does not exist.</summary>
    [Effect(Effects.None)]
    public bool TryGetStored<T>(Entity entity, out T value)
        where T : struct, IComponent
    {
        ref var found = ref LocateStored<T>(entity, registry.IndexOf<T>(), out _);
        if (Unsafe.IsNullRef(ref found))
        {
            value = default;
            return false;
        }
        value = found;
        return true;
    }

    /// <summary>The component by <c>ref readonly</c>, or an empty optional when the entity lacks it, holds it disabled, or does not
    /// exist.</summary>
    [Effect(Effects.None)]
    public Optional<T> Find<T>(Entity entity)
        where T : struct, IComponent
    {
        if (!checkBorrows)
            return new Optional<T>(ref Read<T>(entity));
        var type = registry.IndexOf<T>();
        ref var value = ref Locate<T>(entity, type, out var location);
        IMoveCount? storage = location.Kind switch
        {
            LocationKind.Column => this,
            LocationKind.Sparse => location.Sparse,
            LocationKind.Overlay => stage.Payloads<T>(type),
            _ => null,
        };
        return new Optional<T>(ref value, BorrowCheck.Of(storage));
    }

    /// <summary>A scoped write of the present component: the row is stamped on dispose only if its value changed.</summary>
    /// <exception cref="InvalidOperationException">The entity lacks <typeparamref name="T"/>, holds it disabled, or does not
    /// exist.</exception>
    [Effect(Effects.Mutates)]
    public ComponentEdit<T> Edit<T>(Entity entity)
        where T : struct, IMutableComponent
    {
        CheckWritable();
        ref var value = ref Locate<T>(entity, registry.IndexOf<T>(), out var location);
        if (Unsafe.IsNullRef(ref value))
            ThrowMissing<T>(entity);
        return new ComponentEdit<T>(this, ref value, location, entity);
    }

    /// <summary>An edit of the component, or one without a value when the entity lacks it, holds it disabled, or does not exist.</summary>
    [Effect(Effects.Mutates)]
    public ComponentEdit<T> FindEdit<T>(Entity entity)
        where T : struct, IMutableComponent
    {
        CheckWritable();
        ref var value = ref Locate<T>(entity, registry.IndexOf<T>(), out var location);
        return Unsafe.IsNullRef(ref value)
            ? default
            : new ComponentEdit<T>(this, ref value, location, entity);
    }

    /// <summary>An edit of the component, which is first set to <paramref name="initial"/>, and so enabled, when the entity lacks it or
    /// holds it disabled.</summary>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public ComponentEdit<T> EditOrAdd<T>(
        Entity entity,
        in T initial = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IMutableComponent
    {
        if (!Has<T>(entity))
            Set(entity, initial, file: file, line: line);
        return Edit<T>(entity);
    }

    /// <summary>Adds or overwrites, enabling a disabled one; an overwrite stamps only if the value differs. An add or enable waits inside a
    /// query walk, until the walk closes, and inside an observer, until the next playback; reads see it at once.</summary>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public void Set<T>(
        Entity entity,
        in T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent
    {
        At(file, line);
        Write(entity, registry.IndexOf<T>(), value);
        ThrowIndexConflicts();
    }

    /// <summary>Adds or overwrites the component <paramref name="type"/> from <paramref name="value"/>, its bytes in the component's
    /// layout, as <see cref="Set{T}"/> does: a tag takes an empty span, and an add inside a query walk or an observer waits.</summary>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not in this world's registry or holds managed references, or
    /// <paramref name="value"/> is not <see cref="ComponentDescriptor.Size"/> bytes long.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> is a relation or rows type, or the write fails as
    /// <see cref="Set{T}"/> would.</exception>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public void Set(
        Entity entity,
        ComponentDescriptor type,
        ReadOnlySpan<byte> value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        var index = ComponentIndex(type);
        if (type.IsManaged)
            throw new ArgumentException(
                $"{type.Name} holds managed references, so it has no byte layout; set it through Set<T>.",
                nameof(type)
            );
        if (value.Length != type.Size)
            throw new ArgumentException(
                $"{type.Name} takes {type.Size} bytes, not {value.Length}.",
                nameof(value)
            );
        type.Column.Write(this, entity, index, value);
        ThrowIndexConflicts();
    }

    int ComponentIndex(ComponentDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!registry.Contains(type.Index))
            throw new ArgumentException(
                $"{type.Name} is not in this world's registry; pass its assembly's module to Registry.Build.",
                nameof(type)
            );
        CheckComponent(type.Index);
        return type.Index;
    }

    /// <summary>Writes the whole value of a <typeparamref name="T"/> the entity already has, replace-only and indexed components
    /// included. An overwrite stamps only if the value differs.</summary>
    /// <exception cref="InvalidOperationException">The entity has no <typeparamref name="T"/>, or holds it disabled.</exception>
    [Effect(Effects.Mutates)]
    public void Replace<T>(
        Entity entity,
        in T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent
    {
        At(file, line);
        CheckWritable();
        ref var slot = ref Slot(entity);
        var type = registry.IndexOf<T>();
        ref var current = ref LocateStored<T>(entity, type, out var location);
        if (Unsafe.IsNullRef(ref current) || location.Disabled)
            ThrowNotReplaceable<T>(entity, !Unsafe.IsNullRef(ref current));
        if (Records(slot, location, structural: false))
            RecordSet(entity, type, value);
        else
            Overwrite(entity, ref slot, type, ref current, location, value);
        ThrowIndexConflicts();
    }

    internal void Write<T>(Entity entity, int type, in T value)
        where T : struct
    {
        CheckWritable();
        ref var slot = ref Slot(entity);
        ref var current = ref Prepare<T>(entity, type, ref slot, out var location, out var kind);
        switch (kind)
        {
            case WriteKind.Record:
                RecordSet(entity, type, value);
                return;
            case WriteKind.Add:
                AddNew(entity, ref slot, type, value);
                return;
        }
        Overwrite(entity, ref slot, type, ref current, location, value);
        if (kind == WriteKind.Enable)
            Toggle(entity, ref slot, type, true);
    }

    // A batch never moves its entity before it closes, so everything but an overwrite records.
    internal void WriteBatched<T>(Entity entity, in T value, string file, int line)
        where T : struct, IComponent
    {
        At(file, line);
        CheckWritable();
        ref var slot = ref Slot(entity);
        var type = registry.IndexOf<T>();
        ref var current = ref Prepare<T>(entity, type, ref slot, out var location, out var kind);
        if (kind == WriteKind.Overwrite)
            Overwrite(entity, ref slot, type, ref current, location, value);
        else
            RecordSet(entity, type, value);
        ThrowIndexConflicts();
    }

    enum WriteKind : byte
    {
        Overwrite,
        Enable,
        Add,
        Record,
    }

    ref T Prepare<T>(
        Entity entity,
        int type,
        ref EntitySlot slot,
        out ValueLocation location,
        out WriteKind kind
    )
        where T : struct
    {
        ref var current = ref LocateStored<T>(entity, type, out location);
        if (Unsafe.IsNullRef(ref current))
        {
            Structural();
            CheckComponent(type);
            CheckRequires(entity, registry[type]);
            ClaimSingleton(entity, type);
            kind = RecordsStructure(slot) ? WriteKind.Record : WriteKind.Add;
            return ref current;
        }
        if (location.Disabled)
            Structural();
        kind =
            Records(slot, location, structural: location.Disabled) ? WriteKind.Record
            : location.Disabled ? WriteKind.Enable
            : WriteKind.Overwrite;
        return ref current;
    }

    void Overwrite<T>(
        Entity entity,
        ref EntitySlot slot,
        int type,
        ref T current,
        in ValueLocation location,
        in T value
    )
        where T : struct
    {
        var indexed = hasIndexes && layout.IndexBase[type] >= 0;
        switch (location.Kind)
        {
            case LocationKind.Overlay:
            {
                if (
                    !ComponentOpsOf<T>.Equals(
                        ref Unsafe.As<T, byte>(ref current),
                        ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value))
                    )
                )
                    EditedRecorded(entity, type);
                var pending = indexed ? current : default;
                FreeReplacedBuffers(type, ref current, in value);
                current = value;
                if (indexed)
                    RefreshChangedIndexes(entity, type, in pending, in current);
                return;
            }
            case LocationKind.Tag:
                return;
        }
        if (
            ComponentOpsOf<T>.Equals(
                ref Unsafe.As<T, byte>(ref current),
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value))
            )
        )
            return;
        var previous = indexed ? current : default;
        // The hot path holds only for a plain value; a replace-only capture or a buffer free is the applier's.
        if (!layout.ReplaceOnly[type] && !layout.OwnsBuffers[type])
        {
            current = value;
            Stamp(location);
        }
        else
        {
            var state = Kept(type, !location.Disabled, !location.Disabled, Tracking);
            ApplyValue(
                entity,
                ref slot,
                ref state,
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value))
            );
            Report(in state, entity, ref Unsafe.NullRef<byte>(), ref Unsafe.NullRef<byte>());
        }
        if (indexed)
            RefreshChangedIndexes(entity, type, in previous, in current);
    }

    void FreeReplacedBuffers<T>(int type, ref T current, in T value)
        where T : struct
    {
        if (layout.OwnsBuffers[type])
            FreeReplacedBuffers(
                type,
                ref Unsafe.As<T, byte>(ref current),
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value))
            );
    }

    /// <summary>Adds the tag, or enables it when disabled; no-op when present. Inside a query walk either waits until the walk closes, and
    /// inside an observer until the next playback; reads see it at once.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist, lacks a component the tag requires, or the tag is a singleton
    /// another entity holds, enabled or disabled.</exception>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public void Add<T>(
        Entity entity,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, ITag => Set(entity, default(T), file: file, line: line);

    /// <summary><see cref="Set{T}"/> when <paramref name="value"/> has a value, otherwise <see cref="Remove{T}"/>.</summary>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public void SetOrRemove<T>(
        Entity entity,
        in T? value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent
    {
        if (value.HasValue)
            Set(entity, value.GetValueOrDefault(), file: file, line: line);
        else
            Remove<T>(entity, file: file, line: line);
    }

    void AddNew<T>(Entity entity, ref EntitySlot slot, int type, in T value)
        where T : struct
    {
        if (layout.Class[type] is StorageClass.Column or StorageClass.SignatureTag)
            MoveTo(entity, ref slot, WithType(slot.Placed, type));
        AddStored(entity, ref slot, type, ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)));
        RefreshIndexes(entity, type);
    }

    // The entity's archetype already holds a column or signature tag; a sparse type is added to its storage here.
    void AddStored(Entity entity, ref EntitySlot slot, int type, ref byte value)
    {
        var state = Adding(type, Tracking);
        ref var stored = ref ApplyState(entity, ref slot, ref state, ref value);
        Report(in state, entity, ref Unsafe.NullRef<byte>(), ref stored);
    }

    // The value rides with the recorded presence change, so playback reports the two as one net change.
    void RecordSet<T>(Entity entity, int type, in T value)
        where T : struct
    {
        StageSet(entity, type, value);
        RefreshIndexes(entity, type);
    }

    internal void CheckComponent(int type)
    {
        if (layout.Class[type] == StorageClass.None)
            throw new InvalidOperationException(
                $"{registry[type].Name} is a relation or rows type, not a component; use Relate or its rows accessor."
            );
    }

    internal void AddSparse<T>(Entity entity, int type, in T value)
        where T : struct =>
        AddStored(
            entity,
            ref allocator.Slots[entity.Index],
            type,
            ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value))
        );

    /// <summary>Removes the stored component, enabled or disabled; false when the entity stores none or does not exist. Inside a query walk
    /// the removal waits until the walk closes, and inside an observer until the next playback; reads see it at once.</summary>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public bool Remove<T>(
        Entity entity,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent
    {
        At(file, line);
        CheckWritable();
        Structural();
        return RemoveComponent(entity, registry.IndexOf<T>());
    }

    /// <summary>Removes the stored component <paramref name="type"/>, enabled or disabled, as <see cref="Remove{T}"/> does, and returns
    /// false when the entity stores none or does not exist; inside a query walk or an observer the removal waits.</summary>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not in this world's registry.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> is a relation or rows type.</exception>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public bool Remove(
        Entity entity,
        ComponentDescriptor type,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        CheckWritable();
        Structural();
        var index = ComponentIndex(type);
        return RemoveComponent(entity, index);
    }

    bool RemoveComponent(Entity entity, int type)
    {
        if (!allocator.IsCurrent(entity) || !IsStored(entity, type))
            return false;
        ref var slot = ref allocator.Slots[entity.Index];
        if (RecordsStructure(slot))
            RecordRemove(entity, type);
        else
            RemoveStored(entity, ref slot, type);
        return true;
    }

    internal bool RemoveBatched<T>(Entity entity, string file, int line)
        where T : struct, IComponent
    {
        At(file, line);
        CheckWritable();
        Structural();
        var type = registry.IndexOf<T>();
        if (!allocator.IsCurrent(entity) || !IsStored(entity, type))
            return false;
        RecordRemove(entity, type);
        return true;
    }

    void RecordRemove(Entity entity, int type)
    {
        Stage(CommandKind.Remove, entity, type);
        ReleaseSingleton(entity, type);
        RefreshIndexes(entity, type);
    }

    void PropagateRemoval(Entity entity, int type)
    {
        if (layout.PropagatesToAncestors[type] || layout.PropagatesToSources[type])
            Propagate(entity, type, version);
    }

    void RemoveStored(Entity entity, ref EntitySlot slot, int type)
    {
        var column = layout.Class[type] is StorageClass.Column or StorageClass.SignatureTag;
        var enabled =
            !column
            || slot.Placed.EnableSlot(type) is not (var enable and >= 0)
            || slot.Placed.IsEnabled(enable, slot.Row);
        var state = Removing(type, enabled, Tracking);
        ref var departing = ref StoredValue(entity, type);
        Vacating(type, ref departing);
        Report(in state, entity, ref departing, ref Unsafe.NullRef<byte>());
        if (column)
            MoveTo(entity, ref slot, WithoutType(slot.Placed, type));
        ApplyState(entity, ref slot, ref state, ref Unsafe.NullRef<byte>());
        RefreshIndexes(entity, type);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool WatchesRemoval(int type) =>
        layout.OwnsBuffers[type]
        || (observers.AnyRemove && observers.For(type) is { WantsRemove: true });

    void Vacating(int type, ref byte value)
    {
        if (layout.OwnsBuffers[type] && !Unsafe.IsNullRef(ref value))
            FreeBuffers(type, ref value);
    }

    // A destroyed entity's components leave without log entries; observers still hear each present one go.
    internal void Departing(
        Entity entity,
        int type,
        ref byte value,
        LifecycleReason reason,
        bool present = true
    )
    {
        Vacating(type, ref value);
        if (present)
            observers.QueueRemove(
                entity,
                type,
                ref Unsafe.IsNullRef(ref value) ? ref ZeroBytes.Value : ref value,
                reason
            );
    }

    /// <summary>Enables or disables the entity's stored <typeparamref name="T"/>, stamping it if the state changes; disabling one the
    /// entity lacks does nothing. Inside a query walk the flip waits until the walk closes, and inside an observer, or while the entity has
    /// changes waiting for playback, until the next playback; reads see it at once.</summary>
    /// <exception cref="InvalidOperationException">Enabling a <typeparamref name="T"/> the entity lacks (add it with <see cref="Set{T}"/>
    /// or <see cref="Add{T}"/>), re-enabling one breaks a unique index, or the entity does not exist.</exception>
    [Effect(Effects.Structural | Effects.Mutates)]
    public void SetEnabled<T>(
        Entity entity,
        bool enabled,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IEnableable
    {
        At(file, line);
        CheckWritable();
        Structural();
        ref var slot = ref Slot(entity);
        var type = registry.IndexOf<T>();
        if (!Toggle(entity, ref slot, type, enabled) && enabled)
            throw new InvalidOperationException(NoneToEnable(type, entity));
        ThrowIndexConflicts();
    }

    bool Toggle(Entity entity, ref EntitySlot slot, int type, bool enabled)
    {
        if (slot.Pending && overlay.TryGet(entity.Index, type, out var present, out _, out _))
        {
            if (!present)
                return false;
        }
        else
        {
            if (slot.Archetype?.EnableSlot(type) is not (int enable and >= 0))
                return false;
            if (slot.Archetype.IsEnabled(enable, slot.Row) == enabled)
                return true;
            if (!RecordsStructure(slot))
            {
                Flip(entity, ref slot, type, enabled);
                return true;
            }
        }
        Stage(enabled ? CommandKind.Enable : CommandKind.Disable, entity, type);
        RefreshIndexes(entity, type);
        return true;
    }

    void Flip(Entity entity, ref EntitySlot slot, int type, bool enabled)
    {
        var state = Kept(type, !enabled, enabled, Tracking);
        ref var value = ref ApplyValue(entity, ref slot, ref state, ref Unsafe.NullRef<byte>());
        Report(in state, entity, ref value, ref value);
        RefreshIndexes(entity, type);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ref T Read<T>(Entity entity)
        where T : struct
    {
        ref var slot = ref allocator.Current(entity);
        if (!Unsafe.IsNullRef(ref slot) && !slot.Pending && slot.Archetype is { } archetype)
        {
            var at = archetype.Map.IndexOf(ComponentType<T>.Index);
            if (at >= 0 && archetype.Map.SlotAt(at) is { Column: >= 0 } typeSlot)
                return ref typeSlot.Enable < 0 || archetype.IsEnabled(typeSlot.Enable, slot.Row)
                    ? ref archetype.Values<T>(typeSlot.Column)[slot.Row]
                    : ref Unsafe.NullRef<T>();
        }
        ref var found = ref FindSlow<T>(entity, registry.IndexOf<T>(), out var location);
        return ref location.Disabled ? ref Unsafe.NullRef<T>() : ref found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Locate<T>(Entity entity, int type, out ValueLocation location)
        where T : struct
    {
        ref var value = ref LocateStored<T>(entity, type, out location);
        return ref location.Disabled ? ref Unsafe.NullRef<T>() : ref value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T LocateStored<T>(Entity entity, int type, out ValueLocation location)
        where T : struct
    {
        ref var slot = ref allocator.Current(entity);
        if (!Unsafe.IsNullRef(ref slot) && !slot.Pending && slot.Archetype is { } archetype)
        {
            var at = archetype.Map.IndexOf(type);
            if (at >= 0 && archetype.Map.SlotAt(at) is { Column: >= 0 } typeSlot)
            {
                location = new ValueLocation
                {
                    Kind = LocationKind.Column,
                    Archetype = archetype,
                    Row = slot.Row,
                    Tracked = typeSlot.Tracked,
                    Disabled =
                        typeSlot.Enable >= 0 && !archetype.IsEnabled(typeSlot.Enable, slot.Row),
                };
                return ref archetype.Values<T>(typeSlot.Column)[slot.Row];
            }
        }
        return ref FindSlow<T>(entity, type, out location);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal ref T FindSlow<T>(Entity entity, int type, out ValueLocation location)
        where T : struct
    {
        location = default;
        ref var slot = ref allocator.Current(entity);
        if (Unsafe.IsNullRef(ref slot))
            return ref Unsafe.NullRef<T>();
        bool? overlaidEnabled = null;
        var payload = -1;
        if (
            slot.Pending
            && overlay.TryGet(entity.Index, type, out var present, out var enabled, out payload)
        )
        {
            if (!present)
                return ref Unsafe.NullRef<T>();
            overlaidEnabled = enabled;
        }
        if (overlaidEnabled is { } pendingEnabled && payload != PendingOverlay.Stored)
        {
            location.Disabled = !pendingEnabled;
            if (payload < 0)
            {
                location.Kind = LocationKind.Tag;
                return ref TagValue<T>.Value;
            }
            location.Kind = LocationKind.Overlay;
            return ref stage.Payloads<T>(type)[payload];
        }
        return ref Probe<T>(entity, in slot, type, overlaidEnabled, ref location);
    }

    // Storage only, the overlay already consulted: a recorded flip arrives as overlaidEnabled.
    ref T Probe<T>(
        Entity entity,
        scoped in EntitySlot slot,
        int type,
        bool? overlaidEnabled,
        scoped ref ValueLocation location
    )
        where T : struct
    {
        switch (layout.Class[type])
        {
            case StorageClass.Column:
            {
                if (slot.Archetype is not { } archetype)
                    return ref Unsafe.NullRef<T>();
                var at = archetype.IndexOf(type);
                if (at < 0)
                    return ref Unsafe.NullRef<T>();
                ref readonly var typeSlot = ref archetype.SlotAt(at);
                location.Kind = LocationKind.Column;
                location.Archetype = archetype;
                location.Row = slot.Row;
                location.Tracked = typeSlot.Tracked;
                location.FlipRecorded = overlaidEnabled is not null;
                location.Disabled = overlaidEnabled is { } columnEnabled
                    ? !columnEnabled
                    : typeSlot.Enable >= 0 && !archetype.IsEnabled(typeSlot.Enable, slot.Row);
                return ref archetype.Values<T>(typeSlot.Column)[slot.Row];
            }
            case StorageClass.SignatureTag:
            {
                if (
                    slot.Archetype is not { } archetype
                    || archetype.IndexOf(type) is not (var at and >= 0)
                )
                    return ref Unsafe.NullRef<T>();
                ref readonly var tagSlot = ref archetype.SlotAt(at);
                location.Kind = LocationKind.Tag;
                location.Archetype = archetype;
                location.Row = slot.Row;
                location.Tracked = tagSlot.Tracked;
                location.FlipRecorded = overlaidEnabled is not null;
                location.Disabled = overlaidEnabled is { } tagEnabled
                    ? !tagEnabled
                    : tagSlot.Enable >= 0 && !archetype.IsEnabled(tagSlot.Enable, slot.Row);
                return ref TagValue<T>.Value;
            }
            case StorageClass.Sparse:
            {
                if (
                    sparse[layout.SparseSlot[type]] is not { } storage
                    || storage.SlotOf(entity.Index) is not (var at and >= 0)
                )
                    return ref Unsafe.NullRef<T>();
                location.Kind = LocationKind.Sparse;
                location.Sparse = storage;
                location.Row = at;
                location.Tracked = storage.Stamped ? 0 : -1;
                return ref Unsafe.As<SparseStorage<T>>(storage).Values[at];
            }
            case StorageClass.SparseTag:
                if (sparseTags[layout.SparseTagSlot[type]]?.Contains(entity.Index) != true)
                    return ref Unsafe.NullRef<T>();
                location.Kind = LocationKind.Tag;
                return ref TagValue<T>.Value;
        }
        return ref Unsafe.NullRef<T>();
    }

    internal void Stamp(in ValueLocation location)
    {
        if (!Tracking || !location.IsTracked)
            return;
        if (location is { Kind: LocationKind.Column, Archetype: { } archetype })
            archetype.Stamp(location.Tracked, location.Row, version);
        else if (location is { Kind: LocationKind.Sparse, Sparse: { } storage })
            storage.Stamp(location.Row, version);
    }

    internal ulong StampOf<T>(Entity entity, bool added)
        where T : struct
    {
        var type = registry.IndexOf<T>();
        Debug.Assert(registry[type].IsTracked);
        if (Unsafe.IsNullRef(ref Locate<T>(entity, type, out var location)))
            return 0;
        if (location.Kind == LocationKind.Overlay)
        {
            location = default;
            Probe<T>(entity, in allocator.Slots[entity.Index], type, null, ref location);
        }
        return location.Kind switch
        {
            LocationKind.Column
            or LocationKind.Tag when location.Archetype is not null && location.Tracked >= 0 =>
                added
                    ? location.Archetype.AddedAt(location.Tracked, location.Row)
                    : location.Archetype.ChangedAt(location.Tracked, location.Row),
            LocationKind.Sparse when location.Sparse is { } storage && location.Tracked >= 0 =>
                added ? storage.Added[location.Row] : storage.Changed[location.Row],
            _ => 0,
        };
    }

    void CheckRequires(Entity entity, ComponentDescriptor descriptor)
    {
        foreach (var required in descriptor.Requires)
        {
            if (!IsStored(entity, required))
                throw new InvalidOperationException(
                    MissingRequirement(descriptor, required, entity)
                );
        }
    }

    string MissingRequirement(ComponentDescriptor descriptor, int required, Entity entity) =>
        $"{descriptor.Name} requires {registry[required].Name}, which {entity} lacks.";

    SparseStorage StoredSparse(int type) => sparse[layout.SparseSlot[type]] ?? ThrowNoSparse(type);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    SparseStorage ThrowNoSparse(int type) =>
        throw new InvalidOperationException(
            $"{registry[type].Name} has no sparse storage, yet an entity stores it."
        );

    internal unsafe SparseStorage SparseFor(int type)
    {
        ref var storage = ref sparse[layout.SparseSlot[type]];
        if (storage is not null)
            return storage;
        storage = registry[type].Ops.CreateSparse(type);
        if (!registry[type].IsStamped)
            storage.DropStamps();
        if (layout.PropagatesToAncestors[type] || layout.PropagatesToSources[type])
            storage.propagator = this;
        return storage;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ThrowMissing<T>(Entity entity)
    {
        if (!allocator.IsCurrent(entity))
            ThrowNotAlive(entity);
        throw new InvalidOperationException($"{entity} has no {typeof(T).Name}.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowNotReplaceable<T>(Entity entity, bool disabled) =>
        throw new InvalidOperationException(
            disabled
                ? $"{entity}'s {typeof(T).Name} is disabled, so it reads as absent and cannot be replaced; Set writes and enables it."
                : $"{entity} has no {typeof(T).Name} to replace; Replace never adds a component, so add it with Set."
        );

    internal string NoneToEnable(int type, Entity entity) =>
        $"{entity} has no {registry[type].Name} to enable; add it with Set or Add, which also enable a disabled one.";
}

internal static class ZeroBytes
{
    public static byte Value;
}
