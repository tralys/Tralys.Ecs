namespace Tralys.Ecs.Benchmarks;

[Component]
public partial struct BPosition
{
    public float X;
    public float Y;
    public float Z;
}

[Component]
public partial struct BVelocity
{
    public float X;
    public float Y;
    public float Z;
}

[Component]
public partial struct BHealth
{
    public int Value;
}

[Component]
public partial struct Small8
{
    public long A;
}

[Component]
public partial struct Mid32
{
    public long A,
        B,
        C,
        D;
}

[Component]
public partial struct Big128
{
    public long A,
        B,
        C,
        D,
        E,
        F,
        G,
        H,
        I,
        J,
        K,
        L,
        M,
        N,
        O,
        P;
}

[Component]
public partial struct W0
{
    public long A,
        B;
}

[Component]
public partial struct W1
{
    public long A,
        B;
}

[Component]
public partial struct W2
{
    public long A,
        B;
}

[Component]
public partial struct W3
{
    public long A,
        B;
}

[Component]
public partial struct W4
{
    public long A,
        B;
}

[Component]
public partial struct W5
{
    public long A,
        B;
}

[Component]
public partial struct W6
{
    public long A,
        B;
}

[Component]
public partial struct W7
{
    public long A,
        B;
}

[Component]
public partial struct W8
{
    public long A,
        B;
}

[Component]
public partial struct W9
{
    public long A,
        B;
}

[Component]
public partial struct W10
{
    public long A,
        B;
}

[Component]
public partial struct W11
{
    public long A,
        B;
}

[Component]
public partial struct W12
{
    public long A,
        B;
}

[Component]
public partial struct W13
{
    public long A,
        B;
}

[Component]
public partial struct W14
{
    public long A,
        B;
}

[Component]
public partial struct W15
{
    public long A,
        B;
}

[Component, UniqueIndex(nameof(Value))]
public partial struct BKey
{
    public int Value;
}

[Component]
public partial struct BPlainKey
{
    public int Value;
}

[Component, OrderedIndex(KeyFrom = nameof(DueAt), Name = "Due")]
public partial struct BDue
{
    public double At;

    static double DueAt(in BDue due) => due.At;
}

[Component]
public partial struct BPlainDue
{
    public double At;
}

[Relation]
public partial struct BFollows;

[Bundle]
public partial struct NarrowBundle
{
    public BPosition Position;
}

[Bundle]
public partial struct PlayerBundle
{
    public BPosition Position;
    public BVelocity Velocity;
    public BHealth Health;
    public W0 W0;
    public W1 W1;
    public W2 W2;
    public W3 W3;
    public W4 W4;
}

[Bundle]
public partial struct WideBundle
{
    public BPosition Position;
    public W0 W0;
    public W1 W1;
    public W2 W2;
    public W3 W3;
    public W4 W4;
    public W5 W5;
    public W6 W6;
    public W7 W7;
    public W8 W8;
    public W9 W9;
    public W10 W10;
    public W11 W11;
    public W12 W12;
    public W13 W13;
    public W14 W14;
    public W15 W15;
}

static class Bench
{
    public static readonly Registry Registry = Registry.Build(EcsModule.Module);

    public static World World(int chunkCapacity = 0) =>
        Ecs.World.Create(Registry, new WorldOptions { ChunkCapacity = chunkCapacity });

    /// <summary>Sums X over every <see cref="BPosition"/> column, chunk by chunk, the way generated queries will.</summary>
    public static float SumPositions(World world)
    {
        var type = ComponentType<BPosition>.Index;
        var sum = 0f;
        foreach (var archetype in world.archetypes)
        {
            var column = archetype.ColumnSlot(type);
            if (column < 0)
                continue;
            var values = archetype.Values<BPosition>(column);
            for (var start = 0; start < archetype.Count; start += archetype.ChunkCapacity)
            {
                var chunk = values.AsSpan(
                    start,
                    Math.Min(archetype.ChunkCapacity, archetype.Count - start)
                );
                foreach (ref readonly var position in chunk)
                    sum += position.X;
            }
        }
        return sum;
    }
}

[Tag, Enableable]
public partial struct Stunned;
