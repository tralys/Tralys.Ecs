using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal struct EntitySlot
{
    public uint Generation;
    public LifecycleState State;
    public bool Pending;
    public bool Detached;
    public bool DetachedSelf;
    public bool DestroyQueued;
    public Archetype? Archetype;
    public int Row;
    public LifecycleReason DestroyReason;
    public ushort SparseCount;
}

// The free list is FIFO so two worlds running the same operations hand out the same handles.
internal sealed class EntityAllocator
{
    internal EntitySlot[] Slots = new EntitySlot[64];
    int count = 1;
    uint[] free = new uint[16];
    int freeHead;
    int freeCount;

    public int Capacity => count;

    public int InUse => count - 1 - freeCount;

    public Entity Allocate(LifecycleState state)
    {
        uint index;
        if (freeCount > 0)
        {
            index = free[freeHead];
            freeHead = (freeHead + 1) & (free.Length - 1);
            freeCount--;
        }
        else
        {
            if (count == Slots.Length)
                Array.Resize(ref Slots, Slots.Length * 2);
            index = (uint)count++;
            Slots[index].Generation = 1;
        }
        ref var slot = ref Slots[index];
        slot.State = state;
        slot.Pending = false;
        slot.Detached = false;
        slot.DetachedSelf = false;
        slot.DestroyQueued = false;
        slot.Archetype = null;
        slot.DestroyReason = default;
        return new Entity(index, slot.Generation);
    }

    public void Free(uint index)
    {
        ref var slot = ref Slots[index];
        slot.Generation = slot.Generation == uint.MaxValue ? 1 : slot.Generation + 1;
        slot.State = LifecycleState.Dead;
        slot.Pending = false;
        slot.Archetype = null;
        if (freeCount == free.Length)
        {
            var grown = new uint[free.Length * 2];
            for (var i = 0; i < freeCount; i++)
                grown[i] = free[(freeHead + i) & (free.Length - 1)];
            free = grown;
            freeHead = 0;
        }
        free[(freeHead + freeCount) & (free.Length - 1)] = index;
        freeCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsCurrent(Entity entity) => !Unsafe.IsNullRef(ref Current(entity));

    // Slots past count are zeroed, so they read as Dead without a count check.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref EntitySlot Current(Entity entity)
    {
        var slots = Slots;
        if (entity.Index < (uint)slots.Length)
        {
            ref var slot = ref slots[entity.Index];
            if (slot.Generation == entity.Generation && slot.State != LifecycleState.Dead)
                return ref slot;
        }
        return ref Unsafe.NullRef<EntitySlot>();
    }

    // Every slot is freed, not zeroed: a handle taken before the clear must stay dead once its slot is reused.
    public void Clear()
    {
        freeHead = 0;
        freeCount = 0;
        if (free.Length < count)
            free = new uint[BitOperations.RoundUpToPowerOf2((uint)count)];
        for (var index = 1u; index < (uint)count; index++)
        {
            ref var slot = ref Slots[index];
            var generation =
                slot.State == LifecycleState.Dead ? slot.Generation
                : slot.Generation == uint.MaxValue ? 1
                : slot.Generation + 1;
            slot = default;
            slot.Generation = generation;
            free[freeCount++] = index;
        }
    }

    public AllocatorState Export() =>
        new(Slots.AsSpan(0, count).ToArray(), count, [.. FreeOrder()]);

    public void Import(AllocatorState state)
    {
        if (Slots.Length < state.Slots.Length)
            Slots = new EntitySlot[state.Slots.Length];
        Array.Clear(Slots);
        state.Slots.CopyTo(Slots, 0);
        count = state.Count;
        free = new uint[BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, state.Free.Length))];
        state.Free.CopyTo(free, 0);
        freeHead = 0;
        freeCount = state.Free.Length;
    }

    IEnumerable<uint> FreeOrder()
    {
        for (var i = 0; i < freeCount; i++)
            yield return free[(freeHead + i) & (free.Length - 1)];
    }
}

internal sealed record AllocatorState(EntitySlot[] Slots, int Count, uint[] Free);
