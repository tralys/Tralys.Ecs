using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

// Paged: a store per relation kind or row type cannot each hold an array sized by the entity count.
internal sealed class EntityMap
{
    const int PageBits = 10;
    const int PageSize = 1 << PageBits;

    int[]?[] pages = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get(uint index)
    {
        var page = (int)(index >> PageBits);
        if ((uint)page >= (uint)pages.Length || pages[page] is not { } slots)
            return -1;
        return slots[index & (PageSize - 1)] - 1;
    }

    public void Set(uint index, int slot)
    {
        var page = (int)(index >> PageBits);
        if (page >= pages.Length)
            Array.Resize(ref pages, Math.Max(page + 1, Math.Max(4, pages.Length * 2)));
        (pages[page] ??= new int[PageSize])[index & (PageSize - 1)] = slot + 1;
    }

    public void Remove(uint index)
    {
        var page = (int)(index >> PageBits);
        if ((uint)page < (uint)pages.Length && pages[page] is { } slots)
            slots[index & (PageSize - 1)] = 0;
    }

    public void Clear()
    {
        foreach (var page in pages)
        {
            if (page is not null)
                Array.Clear(page);
        }
    }

    public void Load(EntityMap source) => Copies.LoadPages(ref pages, source.pages, PageSize);
}

internal static class Copies
{
    public static void Load<T>(Stack<T> into, Stack<T> from)
    {
        into.Clear();
        if (from.Count == 0)
            return;
        var items = from.ToArray();
        for (var i = items.Length - 1; i >= 0; i--)
            into.Push(items[i]);
    }

    public static void Load<TKey, TValue>(
        Dictionary<TKey, TValue> into,
        Dictionary<TKey, TValue> from
    )
        where TKey : notnull
    {
        into.Clear();
        foreach (var (key, value) in from)
            into.Add(key, value);
    }

    public static void Load<TKey, TValue>(
        ref Dictionary<TKey, TValue>? into,
        Dictionary<TKey, TValue>? from
    )
        where TKey : notnull
    {
        if (from is null)
            into = null;
        else
            Load(into ??= new Dictionary<TKey, TValue>(from.Comparer), from);
    }

    public static void Load<T>(ref T[] into, T[] from, int count)
    {
        if (into.Length < count)
            into = new T[from.Length];
        Array.Copy(from, into, count);
        Array.Clear(into, count, into.Length - count);
    }

    public static void LoadPages(ref int[]?[] into, int[]?[] from, int pageSize)
    {
        if (into.Length < from.Length)
            Array.Resize(ref into, from.Length);
        for (var i = 0; i < into.Length; i++)
        {
            var page = i < from.Length ? from[i] : null;
            if (page is not null)
                page.CopyTo(into[i] ??= new int[pageSize], 0);
            else if (into[i] is { } stale)
                Array.Clear(stale);
        }
    }
}

// A span is valid only until the next edit of any list here: growth and compaction move ranges and reallocate the array.
// A list's stamp outlives the list: a freed id handed to another key keeps counting, so a list read before the reuse still throws.
internal abstract class ListStamps
{
    int[] stamps = new int[8];

    public int StampOf(int list) => stamps[list];

    protected void Track(int list)
    {
        if (list >= stamps.Length)
            Array.Resize(ref stamps, Math.Max(list + 1, stamps.Length * 2));
    }

    protected void Bump(int list) => stamps[list]++;

    protected void BumpAll(int count)
    {
        for (var i = 0; i < count; i++)
            stamps[i]++;
    }
}

internal sealed class ListArena<T> : ListStamps
{
    struct Block
    {
        public int Start;
        public int Count;
        public int Capacity;
    }

    T[] items = new T[16];
    int used;
    int live;
    Block[] blocks = new Block[8];
    int blockCount;
    readonly Stack<int> freeBlocks = new();
    readonly Stack<int>?[] freeRanges = new Stack<int>?[32];

    public void Load(ListArena<T> source)
    {
        var touched = Math.Max(blockCount, source.blockCount);
        if (touched > 0)
            Track(touched - 1);
        BumpAll(touched);
        Copies.Load(ref items, source.items, source.used);
        used = source.used;
        live = source.live;
        Copies.Load(ref blocks, source.blocks, source.blockCount);
        blockCount = source.blockCount;
        Copies.Load(freeBlocks, source.freeBlocks);
        for (var i = 0; i < freeRanges.Length; i++)
        {
            if (source.freeRanges[i] is { } ranges)
                Copies.Load(freeRanges[i] ??= new Stack<int>(), ranges);
            else
                freeRanges[i]?.Clear();
        }
    }

    public int New()
    {
        var id = freeBlocks.Count > 0 ? freeBlocks.Pop() : blockCount++;
        if (id == blocks.Length)
            Array.Resize(ref blocks, blocks.Length * 2);
        blocks[id] = default;
        Track(id);
        return id;
    }

    public int Count(int list) => blocks[list].Count;

    public Span<T> Span(int list)
    {
        ref readonly var block = ref blocks[list];
        return items.AsSpan(block.Start, block.Count);
    }

    public ref T At(int list, int index) => ref items[blocks[list].Start + index];

    public void Add(int list, in T item) => Insert(list, blocks[list].Count, item);

    public void Insert(int list, int index, in T item)
    {
        ref var block = ref blocks[list];
        if (block.Count == block.Capacity)
            Grow(ref block);
        var at = block.Start + index;
        if (index < block.Count)
            Array.Copy(items, at, items, at + 1, block.Count - index);
        items[at] = item;
        block.Count++;
        live++;
        Bump(list);
    }

    public void RemoveAt(int list, int index)
    {
        ref var block = ref blocks[list];
        var at = block.Start + index;
        block.Count--;
        if (index < block.Count)
            Array.Copy(items, at + 1, items, at, block.Count - index);
        items[block.Start + block.Count] = default!;
        live--;
        Bump(list);
        CompactIfSparse();
    }

    public void Free(int list)
    {
        ref var block = ref blocks[list];
        live -= block.Count;
        Release(block);
        block = default;
        Bump(list);
        freeBlocks.Push(list);
        CompactIfSparse();
    }

    public void Clear()
    {
        BumpAll(blockCount);
        Array.Clear(items, 0, used);
        used = 0;
        live = 0;
        blockCount = 0;
        freeBlocks.Clear();
        foreach (var ranges in freeRanges)
            ranges?.Clear();
    }

    void CompactIfSparse()
    {
        if (used > 4 * live + Math.Max(1024, blockCount))
            Compact();
    }

    void Compact()
    {
        var total = 0;
        for (var i = 0; i < blockCount; i++)
            total += Fit(blocks[i].Count);
        var compacted = new T[Math.Max(16, total)];
        var at = 0;
        for (var i = 0; i < blockCount; i++)
        {
            ref var block = ref blocks[i];
            var capacity = Fit(block.Count);
            Array.Copy(items, block.Start, compacted, at, block.Count);
            block.Start = at;
            block.Capacity = capacity;
            at += capacity;
        }
        items = compacted;
        used = at;
        foreach (var ranges in freeRanges)
            ranges?.Clear();
    }

    static int Fit(int count) =>
        count == 0
            ? 0
            : Math.Max(2, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count));

    void Grow(ref Block block)
    {
        var capacity = block.Capacity == 0 ? 2 : block.Capacity * 2;
        var size = System.Numerics.BitOperations.Log2((uint)capacity);
        int start;
        if (freeRanges[size] is { Count: > 0 } ranges)
            start = ranges.Pop();
        else
        {
            start = used;
            used += capacity;
            if (used > items.Length)
                Array.Resize(ref items, Math.Max(used, items.Length * 2));
        }
        Array.Copy(items, block.Start, items, start, block.Count);
        Release(block);
        block.Start = start;
        block.Capacity = capacity;
    }

    void Release(in Block block)
    {
        if (block.Capacity == 0)
            return;
        Array.Clear(items, block.Start, block.Count);
        var size = System.Numerics.BitOperations.Log2((uint)block.Capacity);
        (freeRanges[size] ??= new Stack<int>()).Push(block.Start);
    }
}
