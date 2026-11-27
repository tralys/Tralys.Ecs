using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    /// <summary>Resets the world to empty (entities, ids, generations, sparse data and logs) and keeps its storage capacity. Attachments get OnWorldCleared.</summary>
    /// <exception cref="InvalidOperationException">An iteration or a playback is open.</exception>
    [Effect(
        Effects.Structural
            | Effects.MovesEntities
            | Effects.Mutates
            | Effects.OwnerThread
            | Effects.MovesRowBlocks
    )]
    public void Clear()
    {
        CheckWritable();
        if (iterationDepth > 0 || playing)
            throw new InvalidOperationException(
                "Clear needs no open iteration or playback; call it outside query bodies and observers."
            );
        foreach (var archetype in archetypes)
            archetype.Release();
        allocator.Clear();
        foreach (var storage in sparse)
            storage?.Clear();
        foreach (var tags in sparseTags)
            tags?.Clear();
        Array.Clear(singletonHolders);
        singletonOwner = default;
        DiscardCommands();
        pendingDestroys.Clear();
        structuralCount++;
        observers.Discard();
        changes.BeginEpoch();
        changes.Reset();
        attributed?.Clear();
        hierarchy.Clear();
        foreach (var store in relations)
            store?.Clear();
        foreach (var store in rowStores)
            store?.Clear();
        ClearIndexes();
        ClearBuffers();
        foreach (var attachment in attachmentOrder)
            attachment.OnWorldCleared(this);
    }

    /// <summary>Removes <typeparamref name="T"/> from every entity storing it, enabled or disabled; each removal waits where
    /// <see cref="Remove{T}(Entity, string, int)"/>'s would.</summary>
    [Effect(Effects.Structural | Effects.MovesEntities | Effects.Mutates)]
    public void RemoveAll<T>([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        where T : struct, IComponent
    {
        CheckWritable();
        Structural();
        var type = registry.IndexOf<T>();
        var collect = new Collect([]);
        VisitHolders(type, enabledOnly: false, ref collect);
        var holders = collect.Holders;
        foreach (var index in overlay.Adding(type))
        {
            var pending = new Entity(index, allocator.Slots[index].Generation);
            if (!StoredIn(allocator.Slots[index], pending, type))
                holders.Add(pending);
        }
        foreach (var entity in holders)
            Remove<T>(entity, file: file, line: line);
    }

    internal void ReplayAdds(int type, int recipient)
    {
        var replay = new ReplayTo(observers, type, recipient);
        VisitHolders(type, enabledOnly: true, ref replay);
    }

    interface IHolderVisitor
    {
        void Visit(Entity holder, ref byte value);
    }

    readonly struct Collect(List<Entity> holders) : IHolderVisitor
    {
        public readonly List<Entity> Holders = holders;

        public void Visit(Entity holder, ref byte value) => Holders.Add(holder);
    }

    readonly struct ReplayTo(ObserverRegistry observers, int type, int recipient) : IHolderVisitor
    {
        public void Visit(Entity holder, ref byte value) =>
            observers.QueueReplay(
                holder,
                type,
                ref Unsafe.IsNullRef(ref value) ? ref ZeroBytes.Value : ref value,
                recipient
            );
    }

    // A tag's value is a null ref.
    void VisitHolders<TVisitor>(int type, bool enabledOnly, ref TVisitor visitor)
        where TVisitor : struct, IHolderVisitor
    {
        switch (layout.Class[type])
        {
            case StorageClass.Sparse:
                if (sparse[layout.SparseSlot[type]] is { } storage)
                {
                    for (var i = 0; i < storage.Count; i++)
                        visitor.Visit(storage.Owners[i], ref storage.RowRef(i));
                }
                return;
            case StorageClass.SparseTag:
                if (sparseTags[layout.SparseTagSlot[type]] is { } tags)
                {
                    foreach (var index in tags.Indices())
                        visitor.Visit(
                            new Entity(index, allocator.Slots[index].Generation),
                            ref Unsafe.NullRef<byte>()
                        );
                }
                return;
        }
        foreach (var archetype in archetypes)
        {
            if (!archetype.Signature.Has(type))
                continue;
            var column = archetype.ColumnSlot(type);
            var enable = enabledOnly ? archetype.EnableSlot(type) : -1;
            for (var row = 0; row < archetype.Count; row++)
            {
                if (enable >= 0 && !archetype.IsEnabled(enable, row))
                    continue;
                visitor.Visit(
                    archetype.Entities[row],
                    ref column >= 0 ? ref archetype.RowRef(column, row) : ref Unsafe.NullRef<byte>()
                );
            }
        }
    }
}
