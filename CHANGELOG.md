# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-11-27

### Changed

- A finalisation that would break a `Throw` relation spares the root that breaks it and reports it once; the world's other
  destroys go ahead. It used to leave the whole batch pending and throw on every later playback.
- Destroy observers read a frozen world while finalisation releases its plan: every structural write they make, destroys,
  cancels, reparents and edge and row adds and removes included, records and applies after the release, in the same
  playback, while a value write applies at once. Rescue a dying entity with `CancelDestroy` before finalisation.
- An observer that throws no longer stops a playback: every round and finalisation still runs, and its exception is thrown
  with any others once the playback has finished.
- A recorded edge or reparent whose target is dying or gone when it applies gets that target's policy, and a revival that
  would leave a live `Throw` source onto a dying target is refused.
- A serial `[Query]` body or `[Collect]` predicate may change structure: the change records, through any handle to the
  world, and applies when the outermost walk closes, before `RunX` returns. Rows and edges added or removed in a walk
  record too, so nothing moves while a walk is open, and a walk skips a later row its recorded changes, or an ancestor's
  recorded destroy or detach, take out of the query. A later row's value columns read the value a walk recorded for it.
  Events wait for the next playback. Commands a parallel body recorded never hold a walk back. When the body throws and
  applying what it recorded fails too, a kernel run throws both.
- A parallel body reads through a `WorldReader` parameter and may not take a `World` or `WorldEntity` (TECS0015).
- `[Effect]` flags are the runtime's own, computed by an analyzer on its build and required to match (TECS0047,
  TECS0048); consumer code is never annotated. Borrows are types: a `ref` return, a ref property or a `[Borrow]` ref
  struct. TECS0019 flags a borrow used after a runtime call that moves its storage, or after a call into code handed or
  holding a world handle, or through an interface or overridable member, whose implementation may hold one.
- A `ChangeSet` a watch has filled is empty after the world is cleared or reset, so a mark made by hand after either
  survives the next collect; enumerating one across either throws.
- `ComponentEdit.Value` and `RowEdit.Value` throw once their storage has moved; `Optional` reads do too under
  `WorldOptions.CheckBorrows`, which defaults to on when a module is built with `DEBUG`.
- `Changed<T>` on an inherited tag is rejected (TECS0020).
- A capture image's layout key holds physical layout only, never names, so a renamed type reads through `[Alias]`. Images
  written by 0.1 are refused.
- An observer registered after an event was queued no longer hears it, and removing one twice does nothing.
- `Extract` reports destroy observers' exceptions in `ExtractResult.ObserverFailures` instead of rethrowing them, so the
  image is not lost.
- A watch routes each input's `Hop` from an entity back from detached, pending destroy or a restore, since its changes
  while away reached no route.
- A `ChangeSet` last filled before a `Clear` or `Reset` is emptied first, and a collect asks its scope and routes only
  about entities that exist and are admitted.
- `Playback` throws while an `EntityBatch` is open; it used to land the batch before its dispose.
- A recorded destroy or cancel applies in record order with the batch's reparents and edges, through the same procedure
  as the immediate call, so the log records each destroy and spare as the immediate calls would instead of netting them
  over the playback. A recorded `CancelDestroy` returns whether reads see the entity dying, itself or through an
  ancestor.
- A `SetParent` that would close a cycle with a reparent still waiting throws at the call, recorded or not.
- A waiting `Retarget` counts the edge writes that wait when it checks for the edge.
- A unique-index conflict is settled by write order: a write recorded for later counts at its call, a parallel command at
  the playback that applies it. A playback used to hand the key to whichever write it happened to apply last.

### Added

- `WorldReader`, `EntityReader` and `RowsReader<TKey, TRow>`, reached through `World.Reader`, `WorldEntity.Reader`,
  `ChunkView.Reader` and `ChunkContext.Reader`. A reader's rows enumeration throws if the rows move under it.
- `ChangeWindow`, `ChangeSample.Window` and `ChangeTracking.Commit(CursorId, ChangeWindow)`.
- `Effects.MovesEntities`, `Effects.MovesRowBlocks` and `[Borrow]`.
- `WorldOptions.VerifyInvariants` and `WorldOptions.CheckBorrows`.
- `ExtractResult.ObserverFailures`.

### Removed

- `ChangeTracking.Commit(CursorId, ulong)`, `Effects.MovesRows` and `Effects.Borrows`.
- `[Structural]`, `ChunkView.World` and `ChunkContext.World`, and the analyzers built on consumer effects: TECS0017,
  TECS0038, TECS0043 and TECS0045.
- The two-argument `ComponentModule` constructor, `ChunkContext(World, int, int)`, `UpCache<T>(World)` and
  `Plumbing.ParentTerm<T>(World, Entity)`, which only generated code called: code generated by 0.1 must be regenerated.

### Fixed

- Resetting to a snapshot after resetting to an older one corrupted the world.
- A recorded edge or reparent onto a dying `Throw` target, or a cancel that revived a `Throw` source, wedged every later
  finalising playback.
- A rows enumeration from a parallel query body raced on the block's iteration count.
- A rows enumeration or row edit left open across a `Reset`, or the entity's release, unbalanced the block's counts, so a
  later enumeration's removals skipped rows; an edit whose rows the reset dropped wrote where nothing read.
- A watch whose scope read a component threw on every collect after its entity was destroyed, and dead or reissued
  handles kept stale marks after an overflow, a clear or a reset.
- A cursor committing a window from before a reset skipped the writes after it.
- A cancelled create dropped a `DestroySource` cascade onto it; a symmetric `DestroySource` relation left its mirror source
  alive; a source released in an earlier wave than its target made finalisation throw.
- A detach recorded through `Commands` left later reparents unpartitioned, and a recorded detach and reattach logged moves
  that never happened.
- `Changed<T>` on an inherited tag matched every inheriting row on every run.
- A re-enabled tracked component did not sample as added.
- A recorded retarget off a placeholder was dropped; a singleton claim lost to a rival its requirement rejected.
- `Extract` with a throwing destroy observer cut off the children it left behind, and a child a destroy observer parented
  under the subset was cut off without being unparented.
- `CachedQuery.Count()` counted rows a walk's recorded changes skip.
- A placeholder key in a recorded row removal was never resolved, and a recorded edge or row whose apply failed leaked
  the buffers its value held.
- `Unrelate`, `ClearTargets` and `ClearSources` with a dead handle could leave another entity's later changes waiting;
  they now do nothing, as outside a walk, and `Retarget` throws.
- Closing an `EntityBatch` reported only its first failure.
- Reading an image with a parent cycle restored part of it before throwing.
- TECS0019 missed edits, view properties and rows borrows held across a row move, and `SetParent`, `ClearParent`,
  `CancelDestroy` and the `Rows` writes declared no move.
- A value written in place, by `Set` or an edit, over a `Set` recorded inside `Untracked()` was never stamped.
- Removing an indexed component and detaching or reattaching its entity in one playback left the old unique key indexed,
  even after the entity was released.
- A recorded `CancelDestroy` after a recorded `SetParent` or `ClearParent` judged the entity by its old parent.
- An `EntityBatch` that an exception left open kept every later walk from applying, and neither `Reset` nor `Clear`
  recovered the world.
- A playback stamped a component that only an untracked write had changed, when a later tracked write repeated the
  value.
- A recorded `Relate` judged liveness by the settled world, so it refused a `Throw` edge whose end a `CancelDestroy`
  recorded earlier had already spared.

## [0.1.1] - 2026-11-23

### Fixed

- A schedule with an observer allocated a string on every run of each of its sets under native AOT, which builds a
  type's name afresh on each read; it reads the name only when the observer fails to run the set.

## [0.1.0] - 2026-11-23

### Added

- `Tralys.Ecs`: worlds, archetype storage, components, tags, relations, hierarchy, keyed rows, owned buffers,
  indexes, commands, observers, change tracking, capture and restore, with the source generator and analyzers.
- `Tralys.Ecs.Scheduling`: systems in sets, run conditions, cadences, fault quarantine, playback points and schedule
  observers.
- `Tralys.Ecs.Executor.Pool`: `WorkerPool`, dedicated worker threads for parallel queries.
- `Tralys.Ecs.Testing`: `WorldHarness`, `WorldDiff`, `AllocationAssert` and `ShuffledExecutor`.
