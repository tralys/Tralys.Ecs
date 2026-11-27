using System.Runtime.CompilerServices;
using Tralys.Ecs.Tests;

[assembly: Tralys.Ecs.ComponentInstance<Sent<int>>]
[assembly: Tralys.Ecs.ComponentInstance<Sent<Position>>]
[assembly: Tralys.Ecs.ComponentInstance<Sent<Link>>]

namespace Tralys.Ecs.Tests;

[Component, Tracked]
public partial struct Position
{
    public float X;
    public float Y;
}

[Component]
public partial struct Velocity
{
    public float X;
    public float Y;
}

[Component, Tracked]
public partial struct Health
{
    public int Value;
}

[Component, Enableable, Tracked]
public partial struct Burning
{
    public int Ticks;
}

[Tag]
public partial struct Npc;

[Tag, Enableable, Tracked]
public partial struct Frozen;

[Tag, Storage(StorageKind.Sparse)]
public partial struct Rare;

[Component, Storage(StorageKind.Sparse), Tracked]
public partial struct Dialog
{
    public int Page;
}

[Component]
public partial struct Link
{
    public Entity Target;
}

[InlineArray(4)]
public struct EntityArray4
{
    Entity element;
}

[Component]
public partial struct Squad
{
    public int Size;
    public EntityArray4 Members;
}

[Component]
public partial struct Scratchpad
{
    public int Value;
}

[Component, Storage(StorageKind.Sparse)]
public partial struct Footnote
{
    public int Line;
}

[Component, ReplaceOnly]
public partial struct Identity
{
    public int Id;
}

[Component, Singleton]
public partial struct Clock
{
    public long Tick;
}

[Component]
public partial struct Label
{
    public string Text;
}

[Component, Tracked]
[UniqueIndex(nameof(Number), OnConflict = ConflictPolicy.ReplaceExisting)]
public partial struct Badge
{
    public int Number;
}

[Component, MultiIndex(nameof(Map))]
public partial struct Placement
{
    public int Map;
    public int Cell;
}

[Component, OrderedIndex(KeyFrom = nameof(DueAt), Name = "Due")]
public partial struct Deadline
{
    public double At;

    static double DueAt(in Deadline deadline) => deadline.At;
}

[Component]
[UniqueIndex(nameof(Code), OnConflict = ConflictPolicy.KeepExisting)]
[MultiIndex(nameof(Zone))]
[MultiIndex(nameof(Zone), nameof(Code))]
[UniqueIndex(
    nameof(Code),
    PerParent = true,
    OnConflict = ConflictPolicy.ReplaceExisting,
    Name = "ByCodeUnderParent"
)]
[OrderedIndex(KeyFrom = nameof(DueOf), Name = "Due")]
public partial struct Lot
{
    public int Code;
    public int Zone;
    public int Due;

    static bool DueOf(in Lot lot, out int due)
    {
        due = lot.Due;
        return lot.Due >= 0;
    }
}

[Component, Storage(StorageKind.Sparse)]
[UniqueIndex(nameof(Code), OnConflict = ConflictPolicy.KeepExisting)]
public partial struct SparseLot
{
    public int Code;
}

[Component, UniqueIndex(nameof(Code))]
public partial struct Serial
{
    public int Code;
}

[Component, Enableable, UniqueIndex(nameof(Code))]
public partial struct Permit
{
    public int Code;
}

[Component, Enableable, ReplaceOnly]
public partial struct Seal
{
    public int Mark;
}

[Component, Singleton, Enableable]
public partial struct Weather
{
    public int Kind;
}

public struct Ward
{
    public int Rank;
    public Entity Charge;
}

[Component]
public partial struct Escort
{
    public float Distance;
    public Ward Ward;
}

[ComponentCategory(Bit = 0)]
[AttributeUsage(AttributeTargets.Struct)]
public sealed class PersistedAttribute : Attribute;

[Component, Requires<Position>, Persisted]
public partial struct Anchored
{
    public float Length;
}

[Component, Storage(StorageKind.Sparse), ReplaceOnly]
public partial struct Medal
{
    public int Rank;
}

[Tag, Storage(StorageKind.Sparse), Singleton]
public partial struct Leader;

[Component, Requires<Position>]
public partial struct Itinerary
{
    public BufferHandle<int> Stops;
}

[Component, Enableable]
public partial struct Waypoints
{
    public BufferHandle<int> Stops;
}

[Component]
public partial struct Legs
{
    public BufferHandle<int> Current;
    public BufferHandle<int> Queued;
    public BufferHandle<float> Weights;
}

[Component]
public partial struct Roster
{
    public BufferHandle<Entity> Members;
}

[Rows]
public partial struct Leg
{
    [RowKey]
    public int Slot;
    public BufferHandle<int> Stops;
}

[Component]
public partial struct Sent<T>
    where T : struct
{
    public T Payload;
    public int Count;
}

[Relation]
public partial struct Follows;

[Relation(Traits = RelationTraits.Exclusive)]
public partial struct OwnedBy;

[Relation(Traits = RelationTraits.Symmetric)]
public partial struct AlliedWith;

[Relation(OnTargetDestroyed = TargetPolicy.Throw)]
public partial struct Escorts;

[Relation(OnTargetDestroyed = TargetPolicy.DestroySource)]
public partial struct CrewOf;

[Relation(Traits = RelationTraits.Symmetric, OnTargetDestroyed = TargetPolicy.DestroySource)]
public partial struct Bonded;

[Relation(Traits = RelationTraits.Symmetric, OnTargetDestroyed = TargetPolicy.Throw)]
public partial struct Sworn;

[Rows, Tracked]
public partial struct Effect
{
    [RowKey]
    public int Kind;
    public int Stacks;
}

[Rows(ReverseKeyIndex = true)]
public partial struct Membership
{
    [RowKey]
    public int Guild;
}

[Rows(PresenceMask = true)]
public partial struct Seat
{
    [RowKey]
    public int Slot;
}

[Rows]
public partial struct Tally
{
    [RowKey]
    public int Slot;
    public int Count;
}

[Relation]
public partial struct Owes
{
    public int Amount;
}

[Relation]
public partial struct Guides
{
    public BufferHandle<int> Stops;
}

[Tag(Inherited = true), Enableable]
public partial struct Disabled;

[Component, PropagateChanges(ToAncestors = true)]
public partial struct Stats
{
    public int Power;
}

[Component]
public partial struct Cooldown
{
    [Capture(CapturePolicy.Rebase)]
    public double ReadyAt;

    [Capture(CapturePolicy.Skip)]
    public int LocalTicket;

    public int Charges;
}

public struct Leash
{
    public int Length;
    public Entity Holder;
}

[Component]
public partial struct Tether
{
    public int Strength;
    public Leash Leash;
}

[Component]
public partial struct MaybeLink
{
    public Entity? Target;
}

[Bundle]
public partial struct Mover
{
    public Position Position;
    public Velocity Velocity;
    public Npc Npc;
}

[Bundle]
public partial struct LotBundle
{
    public Lot Lot;
    public Position Position;
}

[Bundle]
public partial struct Tethered
{
    public Anchored Anchored;
    public Position Position;
}

[Bundle]
public partial struct RareMover
{
    public Position Position;
    public Rare Rare;
}

[Bundle]
public partial struct Timekeeper
{
    public Clock Clock;
}

static class TestWorlds
{
    public static readonly Registry Registry = Registry.Build(EcsModule.All);

    public static World Create(in WorldOptions options = default) =>
        World.Create(Registry, options with { CheckThread = true, VerifyInvariants = true });

    public static World Logged() => Create(new WorldOptions { AttributedLogCapacity = 1 << 12 });
}
