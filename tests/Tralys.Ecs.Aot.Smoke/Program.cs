using Tralys.Ecs;
using Tralys.Ecs.Downstream.Fixture;

var registry = Registry.Build(Tralys.Ecs.Aot.Smoke.EcsModule.All);
using var world = World.Create(registry);
var failures = new List<string>();

void Check(bool condition, string what)
{
    if (!condition)
        failures.Add(what);
}

var cargo = registry.Get<Cargo>();
Check(cargo.EntityRefOffsets.Length == 1, "Cargo has one entity-reference offset");
Check(cargo.Fields.Count == 2, "Cargo's field table lists both fields");

var owner = world.Create();
var carrier = world.Create(
    new CargoBundle
    {
        Cargo = new Cargo { Weight = 7, Owner = owner },
    }
);
Check(world.Has<Marked>(carrier), "the bundle's tag landed with it");
Check(world.Get<Cargo>(carrier).Owner == owner, "the bundle's value landed with it");

var copy = world.Create();
world.Set(copy, world.Get<Cargo>(carrier));
using (var edit = world.Edit<Cargo>(copy))
    edit.Value.Weight++;
Check(world.Get<Cargo>(copy).Weight == 8, "an edited copy keeps its own value");
Check(world.Get<Cargo>(carrier).Weight == 7, "editing the copy leaves the original");

world.Set(copy, new Note { Code = 3 });
Check(world.Get<Note>(copy).Code == 3, "sparse storage reads back");

Check(CargoQueries.RunTotal(world).Value == 15, "a generated reduction sums every cargo");
CargoQueries.RunLighten(world);
Check(CargoQueries.RunTotal(world).Value == 13, "a generated parallel query writes every row");
Check(
    world.CargoByWeight().Count(6) == 1 && world.CargoByWeight().Count(7) == 1,
    "a parallel ref write moves its index entry at the join"
);
var system = new CargoSystem();
system.Run(world);
Check(system.Seen == 2, "a generated system binds its lookup and runs its query");
Check(system.Heaviest == 1, "a generated system reads an index through its index lookup");
Check(
    new WorldEntity(world, copy).TryAs<CarrierView>(out var view) && view.Cargo.Weight == 7,
    "a generated view reads its part"
);

world.Relate(owner, copy, new Carries());
Check(world.Sources<Carries>(copy).Count == 1, "a relation edge has its reverse entry");
using (var slot = world.ManifestRows(copy).EditOrAdd(3, out _))
    slot.Value.Count = 5;
Check(
    world.ManifestRows(copy).TryGet(3, out var manifest) && manifest.Count == 5,
    "a keyed row reads back"
);
var stops = world.Buffers<int>().Allocate(8);
world.Set(owner, new Route { Stops = stops });
world.Remove<Route>(owner);
Check(world.Buffers<int>().LiveCount == 0, "removing a component frees the buffer it owns");
world.SetParent(copy, carrier);
var shelf = world.Create();
world.Set(
    shelf,
    new Stock
    {
        Code = 41,
        Zone = 1,
        Slot = 2,
        Expires = 5,
    }
);
world.SetParent(shelf, carrier);
var bin = world.Create();
world.Set(
    bin,
    new Stock
    {
        Code = 42,
        Zone = 1,
        Slot = 2,
        Expires = 9,
    }
);
Check(
    world.StockByCode().TryGet(42, out var found) && found == bin,
    "a unique index finds its key"
);
Check(world.StockByZoneCode().Get((1, 41)).Contains(shelf), "a composite key finds its entity");
Check(world.StockByZone().Count(1) == 2, "a multi index lists every holder of a key");
Check(
    world.StockBySlotUnderParent().TryGet((carrier, 2), out var slotted) && slotted == shelf,
    "a per-parent key follows the entity's parent"
);
var expiring = new List<Entity>();
foreach (var entity in world.StockExpiring().Due(6.0))
    expiring.Add(entity);
Check(expiring is [var first] && first == shelf, "an ordered index hands out only due entities");
var parentWeights = new List<int>();
CargoQueries.RunParentWeightForSubtree(world, carrier, parentWeights);
Check(parentWeights is [0, 6], "a subtree run reads each entity's parent cargo");
var depthOrder = new List<Entity>();
CargoQueries.RunByDepth(world, depthOrder);
Check(depthOrder.SequenceEqual([carrier, copy]), "a depth-ordered query runs parents first");

world.Destroy(carrier);
world.Playback();
Check(!world.Exists(copy), "destroying a parent takes its children with it");
Check(!world.Exists(carrier), "a destroyed entity is gone after the end of the tick");

using (var source = World.Create(registry))
using (var target = World.Create(registry))
{
    var holder = source.Create();
    var waypoints = source.Buffers<int>().Allocate(2);
    source.Buffers<int>().Get(waypoints)[1] = 4;
    source.Set(holder, new Route { Stops = waypoints });
    var crate = source.Create();
    source.Set(crate, new Cargo { Weight = 2, Owner = holder });
    source.ManifestRows(crate).Set(new Manifest { Slot = 1, Count = 6 });
    var image = source.Capture([crate]);
    target.Create();
    var restored = target.Restore(image);
    var newCrate = restored.Map(crate);
    var newHolder = restored.Map(holder);
    Check(
        target.Get<Cargo>(newCrate).Owner == newHolder,
        "a restore remaps a reference inside the subset"
    );
    Check(
        target.Buffers<int>().Get(target.Get<Route>(newHolder).Stops)[1] == 4,
        "a restore copies an owned buffer"
    );
    Check(target.ManifestRows(newCrate).Count == 1, "a restore copies keyed rows");
    var baseline = target.Snapshot();
    target.Destroy(newCrate);
    target.Playback();
    target.Reset(baseline);
    Check(
        target.Exists(newCrate) && target.Get<Cargo>(newCrate).Weight == 2,
        "a reset brings back a destroyed entity under its old handle"
    );
}

{
    using var watched = World.Create(
        registry,
        new WorldOptions { AttributedLogCapacity = 64, VerifyReferences = true }
    );
    var writer = SystemId.Next();
    var keeper = watched.Create();
    var held = watched.Create();
    using (watched.BeginSystemRun(writer))
        watched.Set(keeper, new Cargo { Weight = 4, Owner = held });
    Check(
        watched.AttributedLog.TryFindLast(keeper, LogKind.Added, out var added)
            && added.Origin == writer
            && added.ChangedBy.IsKnown,
        "the attributed log names the writing system and line"
    );
    var visitor = new CargoVisitor();
    watched.Introspection.VisitEntity(keeper, ref visitor);
    Check(visitor.Seen == 1 && visitor.Weight == 4, "the entity visitor sees a component by type");
    Check(
        WorldDiff.Compare(watched, watched).Count == 0,
        "a world diffed against itself has no differences"
    );
    watched.Destroy(held);
    var dangling = false;
    try
    {
        watched.Playback();
    }
    catch (DanglingReferenceException)
    {
        dangling = true;
    }
    Check(dangling, "reference verification reports a dead owner after playback");
}

using (var pool = new WorkerPool(workers: 2))
{
    using var pooled = World.Create(registry, new WorldOptions { Executor = pool });
    for (var i = 0; i < 100; i++)
        pooled.Set(pooled.Create(), new Cargo { Weight = 10 });
    var builder = new ScheduleBuilder<SmokeTick>(SmokeSchedule.Declaration);
    var counter = new CargoSystem();
    builder.Add(new LightenSystem());
    builder.Add(counter, c => c.RunIf(static (in RunContext context) => context.World is not null));
    var schedule = builder.Build(pooled);
    schedule.Run(new SmokeTick(0));
    Check(
        CargoQueries.RunTotal(pooled).Value == 900,
        "a schedule runs a parallel system on the worker pool"
    );
    Check(counter.Seen == 100, "a schedule runs a system in the default set after its sets");
    Check(schedule.Timing.ReadAll()[0].Sample.Runs == 1, "the schedule times each run");
}

foreach (var failure in failures)
    Console.Error.WriteLine("FAILED: " + failure);
Console.WriteLine(
    failures.Count == 0 ? "AOT smoke passed" : $"{failures.Count} AOT smoke check(s) failed"
);
return failures.Count == 0 ? 0 : 1;

struct CargoVisitor : IComponentVisitor
{
    public int Seen;
    public int Weight;

    public void Visit<T>(ComponentDescriptor descriptor, in T value)
        where T : struct
    {
        Seen++;
        if (typeof(T) == typeof(Cargo))
            Weight = System
                .Runtime.CompilerServices.Unsafe.As<T, Cargo>(
                    ref System.Runtime.CompilerServices.Unsafe.AsRef(in value)
                )
                .Weight;
    }
}

partial struct WeightSum : IReduction<WeightSum>
{
    public int Value;

    public static WeightSum Identity => default;

    public static WeightSum Combine(in WeightSum left, in WeightSum right) =>
        new() { Value = left.Value + right.Value };
}

static partial class CargoQueries
{
    [Query]
    internal static void Total(in Cargo cargo, ref WeightSum sum) => sum.Value += cargo.Weight;

    [Query(Parallel = true)]
    internal static void Lighten(ref Cargo cargo) => cargo.Weight -= 1;

    [Query(DepthOrdered = true)]
    internal static void ByDepth(Entity entity, in Cargo cargo, [Arg] List<Entity> order) =>
        order.Add(entity);

    [Query]
    internal static void ParentWeight(
        in Cargo cargo,
        Parent<Cargo> parent,
        [Arg] List<int> weights
    ) => weights.Add(parent.HasValue ? parent.Value.Weight : 0);
}

[System]
sealed partial class CargoSystem : SystemBase
{
    Lookup<Cargo> cargoLookup;
    IndexLookup<Cargo> cargoIndexes;
    public int Seen;
    public int Heaviest;

    [Query]
    void Count(Entity entity, in Cargo cargo)
    {
        if (cargoLookup.Has(entity))
            Seen++;
    }

    protected override void OnRun(in RunContext context)
    {
        RunCount(context.World);
        Heaviest = cargoIndexes.ByWeight().Count(7);
    }
}

[View, Part<Cargo>]
readonly partial struct CarrierView;

readonly record struct SmokeTick(double Seconds) : ITickContext;

[SystemSet]
sealed class Work;

[Schedule, Set<Work>]
sealed partial class SmokeSchedule;

[System, InSet<Work>]
sealed partial class LightenSystem : SystemBase
{
    protected override void OnRun(in RunContext context) => CargoQueries.RunLighten(context.World);
}
