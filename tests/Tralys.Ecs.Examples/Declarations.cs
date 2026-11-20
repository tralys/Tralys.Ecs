namespace Tralys.Ecs.Examples;

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

[Component, UniqueIndex(nameof(Code))]
public partial struct Unit
{
    public int Code;
    public int Health;
}

[Tag]
public partial struct Frozen;

[Tag, Enableable]
public partial struct Visible;

[Tag, Storage(StorageKind.Sparse)]
public partial struct Selected;

[Relation(Traits = RelationTraits.Exclusive)]
public partial struct Targets
{
    public int Priority;
}

[Rows]
public partial struct Item
{
    [RowKey]
    public int Slot;
    public int Count;
}

[Component, Singleton]
public partial struct Clock
{
    public double Seconds;
}
