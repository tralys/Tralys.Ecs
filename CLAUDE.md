# Tralys.Ecs

A source-generated C# ECS (.NET 11, NativeAOT, no reflection in the core). Packages: `src/Tralys.Ecs` (core), `Tralys.Ecs.Scheduling`, `Tralys.Ecs.Executor.Pool`, `Tralys.Ecs.Testing`, and `Tralys.Ecs.Generators` (generator and analyzers).

## Build and verify

- `dotnet build Tralys.Ecs.slnx` — warnings are errors.
- `dotnet run --project tests/Tralys.Ecs.Tests --no-build`, and the same for `tests/Tralys.Ecs.Generators.Tests` and `tests/Tralys.Ecs.Scheduling.Tests`.
- `dotnet run --project tests/Tralys.Ecs.Aot.Smoke --no-build`; CI also runs `dotnet publish tests/Tralys.Ecs.Aot.Smoke -c Release` and the published binary.
- Format with `dotnet csharpier format <files>`; CI runs `dotnet csharpier check .`.

## Rules the build enforces

- **Public API listing.** Each package has `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`. A public change fails the build (RS0016/RS0017) until the list is updated: `dotnet format analyzers <csproj> --diagnostics RS0016 --severity warn`, then delete stale lines by hand.
- **Doc contracts.** Every public member in the four runtime packages needs a `///` summary (CS1591 is an error there). One line: what it does, what throws, when a write applies.
- **Effects.** Every public method of `World`, `WorldEntity`, `Lookup`, `RefLookup`, `Rows`, `Commands`, `BufferArena`, `ChangeTracking`, `WorldScratch` and `EntityBatch` carries `[Effect(...)]` (`Tralys.Ecs.Generated`); `EffectCoverageTests` fails otherwise. The analyzers check query bodies from these flags, so a wrong flag silently lets a bad body through or rejects a good one. There are no name lists to update.
- **Must-dispose types** carry `[MustDispose("consequence")]`; TECS0032 reports an instance that is neither disposed nor handed on.

## Verb grammar

The same verbs mean the same thing on `World`, `WorldEntity`, lookups, `Rows` and generated views:

| verb | meaning |
|---|---|
| `Has` | present: stored and, for an `[Enableable]` type, enabled |
| `Get` | `ref readonly` read; throws if absent |
| `TryGet` | copy out; false if absent |
| `Find` | optional `ref readonly` read (`Optional<T>`) |
| `Edit` | scoped `ref` write; must be present; stamps and reindexes on dispose only if changed |
| `FindEdit` | as `Edit`, or an edit without a value |
| `EditOrAdd` | sets the initial value first if absent |
| `Set` | add or overwrite; enables a disabled one |
| `Replace` | overwrite; must be present |
| `Remove` | remove, enabled or disabled; false if not stored |
| `SetEnabled` | flip a stored `[Enableable]` one; `false` on a missing one does nothing, `true` throws |
| `HasStored`, `TryGetStored` | as `Has`/`TryGet`, but a disabled one counts |

A disabled `[Enableable]` component or tag is absent to every verb but the `Stored` reads, as it is to queries, indexes, views, lookups and change samples; add and remove observers see an enable as an add and a disable as a removal, and the log keeps `Enabled`/`Disabled`. An immediate write reports itself; a playback reports each component's net change over the batch, from one rule both share (`ComponentChange`). Grant with `Add<T>()` (tags) or `Set` (components), which also enable a disabled one; revoke with `SetEnabled<T>(false)`; read with `Has`/`TryGet`. Nothing needs to be born disabled.

Where two spellings exist, write the first: `TryGet` over `Find` when a copy will do; `Set` over `Add<T>()` only for components with a value (tags use `Add<T>()`).

A handle is usable while `IsLive`: it exists and neither it nor an ancestor is pending destroy. `Entity.IsDefault` only says the handle was never assigned. `World.Exists` and `WorldEntity.State` also see a pending-destroy entity, for code that reads a dying one on purpose.

`Get`, `Find` and view getters hand out readonly references. A struct method that is not `readonly`, called through one, runs on a copy and its write is lost, so TECS0046 rejects it: write through `Edit`, or mark the method `readonly` when it changes nothing.

## When writes apply

- Outside a walk, `World` writes apply at once.
- A serial `[Query]` body and a `[Collect]` predicate make no structural change: no add, remove, enable, disable, create, destroy, cancel-destroy, detach, reattach, reparent, `Batch`, or `Set` that adds or enables. TECS0045 reports one at compile time, and at run time it throws `InvalidOperationException`, which also catches a helper the body calls. They read anything and write values at once: their own `ref`/`OptionalRef` columns, `Edit`/`Replace`/`Set` of a component present and enabled, rows and relation edges. A structural change goes in the `[Collect]` act, which runs once the walk has closed, its world writes applying at once.
- A parallel body writes its own `ref` columns and records structural changes through its `Commands` parameter, which only a parallel body takes (TECS0015; the world API is off limits, TECS0017). They apply at the next playback, and its reads see none of them. `Commands.Create` returns a placeholder valid only in that buffer (TECS0011).
- An observer's creates, adds, removes, enables, disables and detaches record and apply later in the same playback; its destroys, cancels and reparents apply at once, so a destroy observer can rescue an entity. Its reads, like those inside an open `EntityBatch`, see a recorded add, remove, enable or disable at once through the pending overlay. An entity left with changes waiting keeps them in order: until they apply, its later structural changes record too. A recorded destroy is checked against target policies when it applies: at playback, after the batch's reparents, or at its `EntityBatch`'s dispose.
- Relation edges and rows apply at once, except those naming an entity an observer created, which wait for it, as do later edge writes on either end of a waiting edge.
- `Destroy` marks pending; the entity stays readable until a finalising playback. `StateOf` reports `PendingDestroy` for its descendants too.
- A change sample skips what queries skip: `Changed<T>()`, `ChangedRows<TKey, TRow>()` and the log's typed entries leave out an entity (for rows, the owner) pending destroy or detached as the sample is read, unless `CursorOptions.IncludePendingDestroy`/`IncludeDetached` opt in, and `Changed<T>()` leaves out a disabled one. Entries about the entity itself always pass, so a skipping consumer resyncs an entity on `Reattached` or `DestroyCancelled`.
- A consumer that turns changes into per-entity work declares a `ChangeWatch` (`Changes.Watch<TBits, TScope>`) rather than walking a sample: each input names its type, the bits it owes and its `Hop` to the entity that owes them; the watch routes value stamps, row stamps and log entries alike, marks `TScope.Whole` for an entity back from detached, pending destroy or a restore and `TScope.Everything` when the log was trimmed, and forgets destroyed entities. `Descendants` keeps a per-owner `DescendantVersionOf` compare, since the log names no losing parent.
- `ChangedRows<TKey, TRow>()` is the rows counterpart of `Changed<T>()`, with the same window (after `Since`, up to `Until`): each row stamped in it with its owner and key, a row added in it included (`Added`). Removals are the log's `RowRemoved`. An `[Untracked]` rows type or a write inside `Untracked()` stamps nothing, so it is never reported, though its adds and removes are still logged; a recorded row write keeps the scope it was written in. It visits only owners with a row stamped after `Since`. One walk per rows type may be open at a time: a second, from any sample, throws until the first is disposed, because starting a walk reorders the chain an open one follows.

## Declarations

- `[Component]`, `[Tag]`, `[Relation]`, `[Rows]` with a `[RowKey]` field. A type's identity is its fully qualified name, which must be unique across every loaded module (TECS0002); a closed generic is declared with `[assembly: ComponentInstance<T>]`.
- Options: `[Enableable]`, `[Storage(StorageKind.Sparse)]`, `[ReplaceOnly]`, `[Singleton]`, `[Untracked]`, `[PropagateChanges(...)]`, `[UniqueIndex]`/`[MultiIndex]`/`[OrderedIndex]`, and `[Rows(PresenceMask = true, ReverseKeyIndex = true)]`.
- Relation and row types are not components: component verbs reject them at compile time.
- `CaptureImage.Write`/`Read` store values as laid out, so reading refuses a type whose name, `SchemaVersion` or layout changed since the write, and a managed type has no byte form. Bump `SchemaVersion` when a field's meaning changes but its layout does not.
- Systems: `[System] partial class X : SystemBase` overriding `OnRun`, with `[Query]` methods called through the generated `RunX`. Nothing about a system's access is declared or inferred: a schedule never runs two systems at once, so its order is total. A helper that changes structure through the `World` it takes is `[Structural]`, so a serial body or `[Collect]` predicate may not call it, and a parallel body only if it also takes a `Commands` (TECS0045). A schedule (`[Schedule, Set<...>]`) runs its sets in declaration order and each set's systems in the order they were added (`ScheduleBuilder.Add`, `Schedule.Add`), then the systems in no set, in the order they were added; it requires every system in its sets to be added; build it per world with `ScheduleBuilder.Build(world)`. After each system that runs, if it left recorded commands or observer events, the schedule plays back there (non-finalising, reported as `After{System}`), so the next system sees its changes; a named `[PlaybackPoint]` sits right after its `After` target or right before its `Before` target, and `Run`, `RunSet<TSet>` and `RunSystem<T>` each end with a playback; `ScheduleBuilder.Observe` takes an `IScheduleObserver` that wraps each set, system run and playback (profiler zones, tracing) and hears each skip. `ScheduleBuilder.ConfigureSet<TSet>(s => s.RunIf(...))` gates every system of a set, those added after the build too: it is checked once per run of the set (`Run`, `RunSet<TSet>`, and `RunSystem<T>` of a member) before each member's quarantine, condition and cadence; a false or throwing one (`IFaultSink.OnSetFault`) skips each member as `SystemSkip.Condition`, leaving its cadence and quarantine probe untouched, and never skips a playback point, since a point also applies and finalises what systems outside the set recorded. Bootstrap with `Registry.Build(EcsModule.All)`.

## Generated code

The generator emits into the consumer assembly; `Tralys.Ecs.Generated` holds the runtime plumbing it calls (`Plumbing`, factories, `[Effect]`). Hand-written code does not use that namespace. `tests/Tralys.Ecs.Aot.Smoke/Generated/` is checked in: its diff shows what each emitter change does to real output.
