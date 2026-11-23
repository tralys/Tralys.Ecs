# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
