namespace Tralys.Ecs.Downstream.Fixture;

[Component, MultiIndex(nameof(Weight))]
public partial struct Cargo
{
    public int Weight;
    public Entity Owner;
}

[Tag]
public partial struct Marked;

[Component, Storage(StorageKind.Sparse)]
public partial struct Note
{
    public int Code;
}

[Bundle]
public partial struct CargoBundle
{
    public Cargo Cargo;
    public Marked Marked;
}

[Relation]
public partial struct Carries;

[Rows]
public partial struct Manifest
{
    [RowKey]
    public int Slot;
    public int Count;
}

[Component]
public partial struct Route
{
    public BufferHandle<int> Stops;
}

[Component]
[UniqueIndex(nameof(Code))]
[MultiIndex(nameof(Zone), nameof(Code))]
[MultiIndex(nameof(Zone))]
[UniqueIndex(nameof(Slot), PerParent = true, Name = "BySlotUnderParent")]
[OrderedIndex(KeyFrom = nameof(ExpiryOf), Name = "Expiring")]
public partial struct Stock
{
    public int Code;
    public int Zone;
    public int Slot;
    public double Expires;

    static double ExpiryOf(Stock stock) => stock.Expires;
}
