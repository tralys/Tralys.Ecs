using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

public sealed partial class World
{
    /// <summary>Resets the world to empty (entities, ids, generations, sparse data and logs) and keeps its storage capacity. Attachments get OnWorldCleared.</summary>
    /// <exception cref="InvalidOperationException">An iteration or a playback is open.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
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

    /// <summary>Removes <typeparamref name="T"/> from every entity storing it, enabled or disabled.</summary>
    /// <exception cref="InvalidOperationException">A serial query body is running.</exception>
    [Effect(Effects.Structural | Effects.MovesRows | Effects.Mutates)]
    public void RemoveAll<T>([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        where T : struct, IComponent
    {
        CheckWritable();
        CheckStructural();
        var type = registry.IndexOf<T>();
        var holders = new List<Entity>();
        switch (layout.Class[type])
        {
            case StorageClass.Sparse:
                if (sparse[layout.SparseSlot[type]] is { } storage)
                    holders.AddRange(storage.Owners.AsSpan(0, storage.Count));
                break;
            case StorageClass.SparseTag:
                if (sparseTags[layout.SparseTagSlot[type]] is { } tags)
                    holders.AddRange(HoldersOf(tags));
                break;
            default:
                foreach (var archetype in archetypes)
                {
                    if (!archetype.Signature.Has(type))
                        continue;
                    holders.AddRange(archetype.Entities.AsSpan(0, archetype.Count));
                }
                break;
        }
        foreach (var index in overlay.Adding(type))
        {
            var pending = new Entity(index, allocator.Slots[index].Generation);
            if (!StoredIn(allocator.Slots[index], pending, type))
                holders.Add(pending);
        }
        foreach (var entity in holders)
            Remove<T>(entity, file: file, line: line);
    }
}
