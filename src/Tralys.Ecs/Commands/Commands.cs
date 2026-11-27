using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>The structural API of a parallel query body: each call records into the body's own buffer and applies at the next playback,
/// and <see cref="Create(string, int)"/> returns a placeholder valid only inside that buffer.</summary>
public readonly ref struct Commands
{
    readonly CommandBuffer buffer;

    internal Commands(CommandBuffer buffer) => this.buffer = buffer;

    /// <summary>Creates an entity as <see cref="World.Create(string, int)"/> does at the next playback, and returns a placeholder valid
    /// only in this buffer.</summary>
    [Effect(Effects.Structural)]
    public Entity Create([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        buffer.At(file, line);
        return buffer.NewPlaceholder();
    }

    /// <summary>Creates an entity with the whole bundle at the next playback, and returns a placeholder valid only in this
    /// buffer.</summary>
    [Effect(Effects.Structural)]
    public Entity Create<TBundle>(
        in TBundle bundle,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where TBundle : struct, IBundle
    {
        buffer.At(file, line);
        var entity = buffer.NewPlaceholder();
        var writer = new BundleWriter(buffer, entity);
        bundle.Write(ref writer);
        foreach (var type in BundleTags<TBundle>.Of(buffer.world.registry))
            buffer.Record(CommandKind.Set, entity, type, -1);
        return entity;
    }

    /// <summary>Marks the entity pending destroy as <see cref="World.Destroy"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Destroy(
        Entity entity,
        LifecycleReason reason = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        buffer.At(file, line);
        buffer.CheckLive(entity);
        buffer.Record(CommandKind.Destroy, entity, reason: reason);
    }

    /// <summary>Takes the entity out of pending destroy as <see cref="World.CancelDestroy"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void CancelDestroy(
        Entity entity,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        buffer.At(file, line);
        buffer.CheckLive(entity);
        buffer.Record(CommandKind.CancelDestroy, entity);
    }

    /// <summary>Adds the tag as <see cref="World.Add{T}"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Add<T>(
        Entity entity,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, ITag => Set(entity, default(T), file: file, line: line);

    /// <summary><see cref="Set{T}"/> when <paramref name="value"/> has a value, otherwise <see cref="Remove{T}"/>.</summary>
    [Effect(Effects.Structural)]
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

    /// <summary>Adds or overwrites as <see cref="World.Set{T}"/> does, at the next playback, which rejects an add that breaks a
    /// requirement or singleton claim.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Set<T>(
        Entity entity,
        in T value,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent
    {
        buffer.At(file, line);
        buffer.CheckLive(entity);
        var type = buffer.world.registry.IndexOf<T>();
        buffer.world.CheckComponent(type);
        buffer.RecordSet(entity, type, value);
    }

    /// <summary>Removes the component as <see cref="World.Remove{T}"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Remove<T>(
        Entity entity,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IComponent
    {
        buffer.At(file, line);
        buffer.CheckLive(entity);
        buffer.Record(CommandKind.Remove, entity, buffer.world.registry.IndexOf<T>());
    }

    /// <summary>Enables or disables the component as <see cref="World.SetEnabled{T}"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void SetEnabled<T>(
        Entity entity,
        bool enabled,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where T : struct, IEnableable
    {
        buffer.At(file, line);
        buffer.CheckLive(entity);
        buffer.Record(
            enabled ? CommandKind.Enable : CommandKind.Disable,
            entity,
            buffer.world.registry.IndexOf<T>()
        );
    }

    /// <summary>Detaches the entity as <see cref="World.Detach"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Detach(
        Entity entity,
        LifecycleReason reason = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        buffer.At(file, line);
        buffer.CheckLive(entity);
        buffer.Record(CommandKind.Detach, entity, reason: reason);
    }

    /// <summary>Reattaches the entity as <see cref="World.Reattach"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Reattach(
        Entity entity,
        LifecycleReason reason = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        buffer.At(file, line);
        buffer.CheckLive(entity);
        buffer.Record(CommandKind.Reattach, entity, reason: reason);
    }

    /// <summary>Adds the edge or replaces its payload as <see cref="World.Relate{R}"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">Either entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Relate<R>(
        Entity source,
        Entity target,
        in R payload = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        buffer.At(file, line);
        buffer.CheckLive(source);
        buffer.CheckLive(target);
        buffer.RecordValue(CommandKind.Relate, source, payload, target);
    }

    /// <summary>Removes the edge as <see cref="World.Unrelate{R}"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">Either entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Unrelate<R>(
        Entity source,
        Entity target,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        buffer.At(file, line);
        buffer.CheckLive(source);
        buffer.CheckLive(target);
        buffer.Record(
            CommandKind.Unrelate,
            source,
            buffer.world.registry.IndexOf<R>(),
            other: target
        );
    }

    /// <summary>Moves the edge to <paramref name="newTarget"/>, keeping its payload, as <see cref="World.Retarget{R}"/> does, at the next
    /// playback.</summary>
    /// <exception cref="InvalidOperationException">An entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Retarget<R>(
        Entity source,
        Entity oldTarget,
        Entity newTarget,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        buffer.At(file, line);
        buffer.CheckLive(source);
        buffer.CheckLive(oldTarget);
        buffer.CheckLive(newTarget);
        buffer.Record(
            CommandKind.Retarget,
            source,
            buffer.world.registry.IndexOf<R>(),
            other: newTarget,
            oldTarget: oldTarget
        );
    }

    /// <summary>Removes every <typeparamref name="R"/> edge from <paramref name="source"/>, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void ClearTargets<R>(
        Entity source,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        buffer.At(file, line);
        buffer.CheckLive(source);
        buffer.Record(CommandKind.ClearTargets, source, buffer.world.registry.IndexOf<R>());
    }

    /// <summary>Removes every <typeparamref name="R"/> edge to <paramref name="target"/>, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void ClearSources<R>(
        Entity target,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
        where R : struct, IRelation
    {
        buffer.At(file, line);
        buffer.CheckLive(target);
        buffer.Record(CommandKind.ClearSources, target, buffer.world.registry.IndexOf<R>());
    }

    /// <summary>Makes <paramref name="child"/> a child of <paramref name="parent"/> at <paramref name="index"/> as
    /// <see cref="World.SetParent"/> does, at the next playback.</summary>
    /// <exception cref="ArgumentException"><paramref name="parent"/> is null; use <see cref="ClearParent"/>.</exception>
    /// <exception cref="InvalidOperationException">An entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void SetParent(
        Entity child,
        Entity parent,
        int index = -1,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        World.CheckParent(parent);
        Reparent(child, parent, index, file, line);
    }

    /// <summary>Makes <paramref name="child"/> a root as <see cref="World.ClearParent"/> does, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void ClearParent(
        Entity child,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    ) => Reparent(child, default, -1, file, line);

    void Reparent(Entity child, Entity parent, int index, string file, int line)
    {
        buffer.At(file, line);
        buffer.CheckLive(child);
        if (!parent.IsDefault)
            buffer.CheckLive(parent);
        buffer.Record(CommandKind.SetParent, child, payload: index, other: parent);
    }

    internal CommandRows<TKey, TRow> Rows<TKey, TRow>(Entity entity)
        where TKey : notnull
        where TRow : struct, IRow<TKey> => new(buffer, entity);
}

/// <summary>Row changes on one entity, recorded the way <see cref="Commands"/> records component changes.</summary>
public readonly ref struct CommandRows<TKey, TRow>
    where TKey : notnull
    where TRow : struct, IRow<TKey>
{
    readonly CommandBuffer buffer;
    readonly Entity entity;

    internal CommandRows(CommandBuffer buffer, Entity entity)
    {
        this.buffer = buffer;
        this.entity = entity;
    }

    /// <summary>Adds or overwrites the row with <paramref name="row"/>'s key, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Set(in TRow row, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        buffer.At(file, line);
        buffer.CheckLive(entity);
        buffer.RecordValue(CommandKind.UpsertRow, entity, row);
    }

    /// <summary>Removes the row keyed <paramref name="key"/>, if any, at the next playback.</summary>
    /// <exception cref="InvalidOperationException">The entity does not exist or is another buffer's placeholder.</exception>
    [Effect(Effects.Structural)]
    public void Remove(
        in TKey key,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        buffer.At(file, line);
        buffer.CheckLive(entity);
        var keyed = default(TRow);
        keyed.RowKey = key;
        buffer.RecordValue(CommandKind.RemoveRow, entity, keyed);
    }
}
