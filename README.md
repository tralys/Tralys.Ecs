# Tralys.Ecs

A source-generated entity component system for .NET. Components are plain structs. At compile time the generator
emits their metadata, queries, systems, views and lookups. The core uses no reflection and is trim- and
NativeAOT-compatible. Analyzers reject a query body that breaks the world's write rules before it runs.

| Package | Role |
|---|---|
| `Tralys.Ecs` | Worlds, archetype storage, components, tags, relations, hierarchy, keyed rows, owned buffers, indexes, commands, observers, change tracking, capture and restore. Carries the generator and analyzers. |
| `Tralys.Ecs.Scheduling` | Schedules: systems in sets, run conditions, cadences, fault quarantine, playback points and schedule observers. |
| `Tralys.Ecs.Executor.Pool` | `WorkerPool`, dedicated worker threads for parallel queries. |
| `Tralys.Ecs.Testing` | `WorldHarness`, `WorldDiff`, `AllocationAssert` and `ShuffledExecutor`, for determinism and allocation tests. |

Targets `net11.0`.

## Install

```sh
dotnet add package Tralys.Ecs
dotnet add package Tralys.Ecs.Scheduling
```

## Declare

```csharp
[Component]
public partial struct Position { public float X; public float Y; }

[Component]
public partial struct Velocity { public float X; public float Y; }

[Component, UniqueIndex(nameof(Code))]
public partial struct Unit { public int Code; public int Health; }

[Tag]
public partial struct Frozen;
```

Each declaration can take options:

- Storage and presence: `[Enableable]`, `[Storage(StorageKind.Sparse)]`, `[Singleton]`, `[ReplaceOnly]`.
- Change tracking: `[Untracked]`, `[PropagateChanges(...)]`.
- Indexes: `[UniqueIndex]`, `[MultiIndex]`, `[OrderedIndex]`.

`[Relation]` declares an edge type, and `[Rows]` with a `[RowKey]` field declares keyed rows owned by an entity. A
type's identity is its fully qualified name, which must be unique across every loaded module.

## Use

```csharp
using var world = World.Create(Registry.Build(EcsModule.All));

var unit = world.Create();
world.Set(unit, new Position { X = 1 });
world.Set(unit, new Unit { Code = 7, Health = 10 });

using (var edit = world.Edit<Position>(unit))
    edit.Value.X += 1;

world.UnitByCode().TryGet(7, out var found);
```

```csharp
public readonly record struct GameTick(double Seconds, float Delta) : ITickContext;

[SystemSet]
public sealed class Simulation;

[Schedule, Set<Simulation>]
public sealed partial class GameSchedule;

[System, InSet<Simulation>]
public sealed partial class MoveSystem : SystemBase
{
    [Query(Parallel = true), Without<Frozen>]
    static void Move(ref Position position, in Velocity velocity, [Arg] float delta)
    {
        position.X += velocity.X * delta;
        position.Y += velocity.Y * delta;
    }

    protected override void OnRun(in RunContext context) =>
        RunMove(context.World, context.Tick<GameTick>().Delta);
}

var builder = new ScheduleBuilder<GameTick>(GameSchedule.Declaration);
builder.Add(new MoveSystem());
var schedule = builder.Build(world);
schedule.Run(new GameTick(seconds, delta));
```

## Verbs

The same verbs mean the same thing on `World`, `WorldEntity`, lookups, `Rows` and generated views.

| Verb | Meaning |
|---|---|
| `Has` | Present: stored and, for an `[Enableable]` type, enabled. |
| `Get` / `TryGet` / `Find` | `ref readonly` read that throws if absent / copy out / optional reference. |
| `Edit` / `FindEdit` / `EditOrAdd` | Scoped `ref` write; it stamps and reindexes on dispose, and only if the value changed. |
| `Set` / `Replace` / `Remove` | Add or overwrite / overwrite a present value / remove. |
| `SetEnabled` | Flip a stored `[Enableable]` value. |
| `HasStored`, `TryGetStored` | As `Has` and `TryGet`, but a disabled value counts. |

A handle is usable while `IsLive`: it exists, and neither it nor an ancestor is pending destroy.

## When writes apply

- **Outside a walk:** writes apply at once.
- **Serial `[Query]` body or `[Collect]` predicate:**
  - Writes values at once; makes no structural change.
  - A structural change goes in the `[Collect]` act, which runs after the walk closes.
  - TECS0045 rejects a structural change at compile time; at run time it throws.
- **Parallel body:**
  - Writes its own `ref` columns.
  - Records structural changes through a `Commands` parameter, applied at the next playback.
  - Can't touch the world API (TECS0017).
- **Destroy:** `Destroy` marks the entity pending. It stays readable until a finalising playback, and an observer can
  still rescue it.
- **Readonly references:** a non-`readonly` struct method called through a `ref readonly` runs on a copy, so
  TECS0046 rejects the call.

## Changes, capture and threading

- `world.Changes` hands out cursors: `Sample(cursor).Changed<T>()` and `ChangedRows<TKey, TRow>()` report what was
  stamped since the cursor's last commit. `Changes.Watch<TBits, TScope>` turns stamps and log entries into per-entity
  work across declared hops.
- `world.Observers` notifies add, remove, replace and destroy observers. An enable reaches them as an add and a
  disable as a removal. An immediate write reports at once; a playback reports each component's net change over the
  batch.
- `Snapshot` and `Reset` rewind a whole world. `Capture` and `Restore` move a subtree between worlds, and
  `CaptureImage.Write` and `Read` persist it. Reading refuses bytes whose type, `SchemaVersion` or layout has
  changed.
- Parallel queries run on `WorldOptions.Executor`, inline by default:
  `World.Create(registry, new WorldOptions { Executor = new WorkerPool() })`.
- A schedule never runs two systems at once, so its order is total. After a system leaves recorded work, the
  schedule plays it back, so the next system sees it.

## Building

```sh
dotnet build Tralys.Ecs.slnx
dotnet run --project tests/Tralys.Ecs.Tests
dotnet run --project tests/Tralys.Ecs.Generators.Tests
dotnet run --project tests/Tralys.Ecs.Scheduling.Tests
dotnet publish tests/Tralys.Ecs.Aot.Smoke -c Release
```

Warnings are errors. Each package tracks its public API in `PublicAPI.*.txt`. Working in the repo:
[CLAUDE.md](CLAUDE.md).

## License

MIT.
