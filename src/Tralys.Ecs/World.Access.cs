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
    public bool Pending;

    public readonly bool IsTracked =>
        (Kind == LocationKind.Column && Tracked >= 0)
        || (Kind == LocationKind.Sparse && Tracked >= 0);
}

public sealed partial class World
{
    internal readonly CommandBuffer stage;
    internal readonly PendingOverlay overlay;
    internal int iterationDepth;

    // Serial walks only; iterationDepth also counts observer delivery.
    internal int walkDepth;
    internal readonly ChangeTracking changes;
    internal readonly ObserverRegistry observers;
    internal readonly List<ICoreHook> coreHooks = [];

    /// <summary>Per-consumer change cursors over this world's row stamps and structural log.</summary>
    public ChangeTracking Changes => changes;

    /// <summary>This world's lifecycle observers; every event reaches them at the next playback point, in operation order.</summary>
    public ObserverRegistry Observers => observers;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool Defers(in EntitySlot slot) =>
        iterationDepth > 0 || slot.Pending || slot.State == LifecycleState.Reserved;

    // Not iterationDepth: a destroy observer must rescue an entity at once while it finalises.
    bool DefersLifecycle(in EntitySlot slot) =>
        slot.State == LifecycleState.Reserved || (slot.Pending && !playing);

    // Not iterationDepth: observers still record structural changes, and a destroy observer rescues at once.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void CheckStructural()
    {
        if (walkDepth != 0)
            ThrowStructuralInWalk();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowStructuralInWalk() =>
        throw new InvalidOperationException(
            "A serial query body or [Collect] predicate cannot change structure (add, remove, enable, disable, create, destroy, detach, reparent); return true from a [Collect] predicate and make the change in its Act, which runs once the walk has closed."
        );

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
    [Effect(Effects.Borrows)]
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
    [Effect(Effects.Borrows)]
    public Optional<T> Find<T>(Entity entity)
        where T : struct, IComponent
    {
        return new Optional<T>(ref Read<T>(entity));
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
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
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

    /// <summary>Adds or overwrites, enabling a disabled one. An overwrite stamps only if the value differs; an add or enable throws inside
    /// a serial query body, and inside an observer waits for the next playback while reads see it at once.</summary>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
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
    /// layout, as <see cref="Set{T}"/> does: a tag takes an empty span, and an add inside an observer defers.</summary>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not in this world's registry or holds managed references, or
    /// <paramref name="value"/> is not <see cref="ComponentDescriptor.Size"/> bytes long.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> is a relation or rows type, or the write fails as
    /// <see cref="Set{T}"/> would.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
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
        Write(entity, registry.IndexOf<T>(), value, add: false);
        ThrowIndexConflicts();
    }

    internal void Write<T>(Entity entity, int type, in T value, bool add = true)
        where T : struct
    {
        CheckWritable();
        ref var slot = ref Slot(entity);
        ref var current = ref LocateStored<T>(entity, type, out var location);
        if (Unsafe.IsNullRef(ref current))
        {
            if (!add)
                ThrowNotReplaceable<T>(entity, false);
            CheckStructural();
            AddNew(entity, ref slot, type, value);
            return;
        }
        if (location.Disabled)
        {
            if (!add)
                ThrowNotReplaceable<T>(entity, true);
            CheckStructural();
        }
        if (location.Pending || (location.Disabled && Defers(slot)))
        {
            RecordSet(entity, ref slot, type, value);
            return;
        }
        Overwrite(entity, type, ref current, location, value);
        if (location.Disabled)
            Toggle(entity, ref slot, type, true);
    }

    void Overwrite<T>(Entity entity, int type, ref T current, in ValueLocation location, in T value)
        where T : struct
    {
        var indexed = hasIndexes && layout.IndexBase[type] >= 0;
        switch (location.Kind)
        {
            case LocationKind.Overlay:
            {
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
        var equal = ComponentOpsOf<T>.Equals(
            ref Unsafe.As<T, byte>(ref current),
            ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value))
        );
        if (equal)
            return;
        var previous = indexed ? current : default;
        FreeReplacedBuffers(type, ref current, in value);
        if ((registry[type].Traits & ComponentTraits.ReplaceOnly) != 0)
        {
            var departing = current;
            current = value;
            var present = !location.Disabled;
            Report(
                ComponentChange.Of(true, present, true, present, replaced: true),
                entity,
                type,
                default,
                ref Unsafe.As<T, byte>(ref departing),
                ref Unsafe.As<T, byte>(ref current)
            );
        }
        else
            current = value;
        Stamp(location);
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

    /// <summary>Adds the tag, or enables it when disabled; no-op when present. Inside an observer either waits for the next playback, and
    /// reads see it at once.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist, lacks a component the tag requires, or the tag is a singleton
    /// another entity holds, enabled or disabled; or a serial query body is running and the tag is absent or disabled.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public void Add<T>(
        Entity entity,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, ITag => Set(entity, default(T), file: file, line: line);

    /// <summary><see cref="Set{T}"/> when <paramref name="value"/> has a value, otherwise <see cref="Remove{T}"/>.</summary>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
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
        CheckComponent(type);
        var descriptor = registry[type];
        CheckRequires(entity, descriptor);
        ClaimSingleton(entity, type);
        if (Defers(slot))
        {
            RecordSet(entity, ref slot, type, value);
            return;
        }
        switch (layout.Class[type])
        {
            case StorageClass.Sparse:
                AddSparse(entity, type, value);
                RefreshIndexes(entity, type);
                return;
            case StorageClass.SparseTag:
                AddSparseTag(entity, ref slot, type);
                return;
        }
        var target = WithType(slot.Archetype!, type);
        MoveTo(entity, ref slot, target);
        ref readonly var typeSlot = ref target.SlotAt(target.IndexOf(type));
        ref var stored = ref Unsafe.As<T, byte>(ref TagValue<T>.Value);
        if (typeSlot.Column >= 0)
        {
            ref var cell = ref target.Values<T>(typeSlot.Column)[slot.Row];
            cell = value;
            stored = ref Unsafe.As<T, byte>(ref cell);
        }
        if (Tracking && typeSlot.Tracked >= 0)
            target.StampAdded(typeSlot.Tracked, slot.Row, version);
        Report(
            ComponentChange.Added,
            entity,
            type,
            default,
            ref Unsafe.NullRef<byte>(),
            ref stored
        );
        RefreshIndexes(entity, type);
    }

    // The value rides with the recorded presence change, so playback reports the two as one net change.
    void RecordSet<T>(Entity entity, ref EntitySlot slot, int type, in T value)
        where T : struct
    {
        var payload = layout.Class[type] is StorageClass.Column or StorageClass.Sparse
            ? stage.Payloads<T>(type).Add(value)
            : -1;
        stage.Record(CommandKind.Set, entity, type, payload);
        overlay.Set(entity.Index, type, true, payload);
        overlay.SetEnabled(entity.Index, type, true);
        slot.Pending = true;
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
        where T : struct
    {
        var storage = (SparseStorage<T>)SparseFor(type);
        ref var cell = ref storage.Add(entity, value, out var at);
        allocator.Slots[entity.Index].SparseCount++;
        if (Tracking && registry[type].IsTracked)
            storage.StampAdded(at, version);
        Report(
            ComponentChange.Added,
            entity,
            type,
            default,
            ref Unsafe.NullRef<byte>(),
            ref Unsafe.As<T, byte>(ref cell)
        );
    }

    void AddSparseTag(Entity entity, ref EntitySlot slot, int type)
    {
        if (
            (sparseTags[layout.SparseTagSlot[type]] ??= new SparseTagStorage(type)).Set(
                entity.Index,
                true
            )
        )
            slot.SparseCount++;
        Report(
            ComponentChange.Added,
            entity,
            type,
            default,
            ref Unsafe.NullRef<byte>(),
            ref Unsafe.NullRef<byte>()
        );
    }

    internal void StageBundled<T>(Entity entity, in T value)
        where T : struct
    {
        var type = registry.IndexOf<T>();
        var payload = layout.Class[type] is StorageClass.Column or StorageClass.Sparse
            ? stage.Payloads<T>(type).Add(value)
            : -1;
        stage.Record(CommandKind.Set, entity, type, payload);
        overlay.Set(entity.Index, type, true, payload);
    }

    /// <summary>Removes the stored component, enabled or disabled; false when the entity stores none or does not exist. Inside an observer
    /// the removal waits for the next playback, and reads see it at once.</summary>
    /// <exception cref="InvalidOperationException">A serial query body is running.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public bool Remove<T>(
        Entity entity,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent
    {
        At(file, line);
        CheckWritable();
        CheckStructural();
        return RemoveComponent(entity, registry.IndexOf<T>());
    }

    /// <summary>Removes the stored component <paramref name="type"/>, enabled or disabled, as <see cref="Remove{T}"/> does, and returns
    /// false when the entity stores none or does not exist; inside an observer the removal defers.</summary>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not in this world's registry.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> is a relation or rows type, or a serial query body is
    /// running.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public bool Remove(
        Entity entity,
        ComponentDescriptor type,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        At(file, line);
        CheckWritable();
        CheckStructural();
        var index = ComponentIndex(type);
        return RemoveComponent(entity, index);
    }

    bool RemoveComponent(Entity entity, int type)
    {
        if (!allocator.IsCurrent(entity) || !IsStored(entity, type))
            return false;
        RemoveType(entity, ref allocator.Slots[entity.Index], type, default);
        return true;
    }

    internal void RemoveType(Entity entity, ref EntitySlot slot, int type, LifecycleReason reason)
    {
        if (Defers(slot))
        {
            stage.Record(CommandKind.Remove, entity, type);
            overlay.Set(entity.Index, type, false, -1);
            slot.Pending = true;
            ReleaseSingleton(entity, type);
            RefreshIndexes(entity, type);
            return;
        }
        switch (layout.Class[type])
        {
            case StorageClass.Sparse:
            {
                var storage = sparse[layout.SparseSlot[type]]!;
                ref var value = ref storage.RowRef(storage.SlotOf(entity.Index));
                Vacating(entity, type, ref value);
                Report(
                    ComponentChange.Removed(true),
                    entity,
                    type,
                    reason,
                    ref value,
                    ref Unsafe.NullRef<byte>()
                );
                if (storage.Remove(entity.Index))
                    slot.SparseCount--;
                ReleaseSingleton(entity, type);
                RefreshIndexes(entity, type);
                return;
            }
            case StorageClass.SparseTag:
                Vacating(entity, type, ref Unsafe.NullRef<byte>());
                Report(
                    ComponentChange.Removed(true),
                    entity,
                    type,
                    reason,
                    ref Unsafe.NullRef<byte>(),
                    ref Unsafe.NullRef<byte>()
                );
                if (sparseTags[layout.SparseTagSlot[type]]!.Set(entity.Index, false))
                    slot.SparseCount--;
                ReleaseSingleton(entity, type);
                return;
        }
        var source = slot.Archetype!;
        var column = source.ColumnSlot(type);
        ref var departing = ref column >= 0
            ? ref source.RowRef(column, slot.Row)
            : ref Unsafe.NullRef<byte>();
        if (WatchesRemoval(type))
            Vacating(entity, type, ref departing);
        var enable = source.EnableSlot(type);
        Report(
            ComponentChange.Removed(enable < 0 || source.IsEnabled(enable, slot.Row)),
            entity,
            type,
            reason,
            ref departing,
            ref Unsafe.NullRef<byte>()
        );
        MoveTo(entity, ref slot, WithoutType(source, type));
        ReleaseSingleton(entity, type);
        RefreshIndexes(entity, type);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool WatchesRemoval(int type) =>
        coreHooks.Count > 0
        || layout.OwnsBuffers[type]
        || (observers.AnyRemove && observers.For(type) is { WantsRemove: true });

    void Vacating(Entity entity, int type, ref byte value)
    {
        foreach (var hook in coreHooks)
            hook.OnRemoving(this, entity, type, ref value);
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
        Vacating(entity, type, ref value);
        if (present)
            observers.QueueRemove(
                entity,
                type,
                ref Unsafe.IsNullRef(ref value) ? ref ZeroBytes.Value : ref value,
                reason
            );
    }

    /// <summary>Enables or disables the entity's stored <typeparamref name="T"/>, stamping it if the state changes; disabling one the
    /// entity lacks does nothing. Inside an observer, or while the entity has changes waiting for playback, the flip waits for the next
    /// playback, and reads see it at once.</summary>
    /// <exception cref="InvalidOperationException">Enabling a <typeparamref name="T"/> the entity lacks (add it with <see cref="Set{T}"/>
    /// or <see cref="Add{T}"/>), re-enabling one breaks a unique index, the entity does not exist, or a serial query body is
    /// running.</exception>
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
        CheckStructural();
        ref var slot = ref Slot(entity);
        if (!Toggle(entity, ref slot, registry.IndexOf<T>(), enabled) && enabled)
            throw NotEnableable<T>(entity);
        ThrowIndexConflicts();
    }

    bool Toggle(Entity entity, ref EntitySlot slot, int type, bool enabled)
    {
        if (slot.Pending && overlay.TryGet(entity.Index, type, out var present, out _, out _))
        {
            if (!present)
                return false;
            stage.Record(enabled ? CommandKind.Enable : CommandKind.Disable, entity, type);
            overlay.SetEnabled(entity.Index, type, enabled);
            RefreshIndexes(entity, type);
            return true;
        }
        if (slot.Archetype?.EnableSlot(type) is not (int enable and >= 0))
            return false;
        var archetype = slot.Archetype;
        if (archetype.IsEnabled(enable, slot.Row) == enabled)
            return true;
        if (Defers(slot))
        {
            stage.Record(enabled ? CommandKind.Enable : CommandKind.Disable, entity, type);
            overlay.Set(entity.Index, type, true, PendingOverlay.Stored);
            overlay.SetEnabled(entity.Index, type, enabled);
            slot.Pending = true;
            RefreshIndexes(entity, type);
            return true;
        }
        archetype.SetEnabled(enable, slot.Row, enabled);
        if (Tracking && archetype.TrackedSlot(type) is var tracked and >= 0)
            archetype.Stamp(tracked, slot.Row, version);
        var column = archetype.ColumnSlot(type);
        ref var value = ref column >= 0
            ? ref archetype.RowRef(column, slot.Row)
            : ref Unsafe.NullRef<byte>();
        Report(ComponentChange.Toggled(enabled), entity, type, default, ref value, ref value);
        RefreshIndexes(entity, type);
        if (layout.InheritedSlot[type] > 0 && hierarchy.HasChildren(entity))
            RefreshChildren(entity);
        return true;
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
    ref T FindSlow<T>(Entity entity, int type, out ValueLocation location)
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
                location.Pending = overlaidEnabled is not null;
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
                location.Pending = overlaidEnabled is not null;
                location.Disabled = overlaidEnabled is { } tagEnabled
                    ? !tagEnabled
                    : tagSlot.Enable >= 0 && !archetype.IsEnabled(tagSlot.Enable, slot.Row);
                return ref TagValue<T>.Value;
            }
            case StorageClass.Sparse:
            {
                var storage = sparse[layout.SparseSlot[type]];
                var at = storage?.SlotOf(entity.Index) ?? -1;
                if (at < 0)
                    return ref Unsafe.NullRef<T>();
                location.Kind = LocationKind.Sparse;
                location.Sparse = storage;
                location.Row = at;
                location.Tracked = registry[type].IsTracked ? 0 : -1;
                return ref Unsafe.As<SparseStorage<T>>(storage!).Values[at];
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
        if (location.Kind == LocationKind.Column)
            location.Archetype!.Stamp(location.Tracked, location.Row, version);
        else
            location.Sparse!.Stamp(location.Row, version);
    }

    internal ulong StampOf<T>(Entity entity, bool added)
        where T : struct
    {
        var type = registry.IndexOf<T>();
        if (Unsafe.IsNullRef(ref Locate<T>(entity, type, out var location)))
            return 0;
        return location.Kind switch
        {
            LocationKind.Column
            or LocationKind.Tag when location.Archetype is not null && location.Tracked >= 0 =>
                added
                    ? location.Archetype.AddedAt(location.Tracked, location.Row)
                    : location.Archetype.ChangedAt(location.Tracked, location.Row),
            LocationKind.Sparse when location.Tracked >= 0 => added
                ? location.Sparse!.Added[location.Row]
                : location.Sparse!.Changed[location.Row],
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

    internal unsafe SparseStorage SparseFor(int type)
    {
        ref var storage = ref sparse[layout.SparseSlot[type]];
        if (storage is not null)
            return storage;
        storage = registry[type].Ops.CreateSparse(type);
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

    static InvalidOperationException NotEnableable<T>(Entity entity) =>
        new(
            $"{entity} has no {typeof(T).Name} to enable; add it with Set or Add, which also enable a disabled one."
        );
}

internal static class ZeroBytes
{
    public static byte Value;
}

internal interface ICoreHook
{
    void OnRemoving(World world, Entity entity, int type, ref byte value);
}
