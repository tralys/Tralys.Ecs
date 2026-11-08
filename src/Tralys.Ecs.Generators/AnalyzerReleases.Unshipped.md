; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
TECS0001 | Tralys.Ecs.Declarations | Error | Declared type must be partial
TECS0002 | Tralys.Ecs.Declarations | Error | Type is declared twice
TECS0004 | Tralys.Ecs.Declarations | Error | Component interface implemented by hand
TECS0005 | Tralys.Ecs.Declarations | Error | Entity held where entity-reference offsets cannot reach it
TECS0006 | Tralys.Ecs.Declarations | Error | Index key type cannot be indexed
TECS0007 | Tralys.Ecs.Declarations | Error | ReplaceOnly component propagates changes
TECS0008 | Tralys.Ecs.Declarations | Error | Entity reference is out of the generator's reach
TECS0009 | Tralys.Ecs.Declarations | Error | Bundle lacks a required component
TECS0010 | Tralys.Ecs.Declarations | Error | Bundle field is not a declared component
TECS0011 | Tralys.Ecs.Usage | Error | Placeholder entity escapes its command buffer
TECS0013 | Tralys.Ecs.Queries | Error | Query is declared where the generator cannot extend it
TECS0014 | Tralys.Ecs.Queries | Error | Query has no positive term
TECS0015 | Tralys.Ecs.Queries | Error | Query parameter is not supported
TECS0016 | Tralys.Ecs.Queries | Error | Query takes ref to a component it may not write in place
TECS0017 | Tralys.Ecs.Queries | Error | Parallel query body changes the world directly
TECS0018 | Tralys.Ecs.Queries | Warning | Query parameter is taken by ref but never written
TECS0019 | Tralys.Ecs.Usage | Warning | Ref from the world used after a structural change
TECS0020 | Tralys.Ecs.Queries | Error | Query declaration is invalid
TECS0021 | Tralys.Ecs.Scheduling | Error | System does not derive from SystemBase
TECS0023 | Tralys.Ecs.Scheduling | Error | Lookup field is readonly
TECS0025 | Tralys.Ecs.Scheduling | Error | Lookup field is never bound
TECS0026 | Tralys.Ecs.Declarations | Error | Index declaration is invalid
TECS0029 | Tralys.Ecs.Scheduling | Error | Set names a type that is not a [SystemSet]
TECS0030 | Tralys.Ecs.Scheduling | Error | Run condition can capture state
TECS0031 | Tralys.Ecs.Declarations | Error | Capture policy is invalid
TECS0032 | Tralys.Ecs.Usage | Error | Scoped handle is not disposed
TECS0034 | Tralys.Ecs.Declarations | Error | View parts share a name
TECS0035 | Tralys.Ecs.Declarations | Error | View part on a type that is not a view
TECS0036 | Tralys.Ecs.Usage | Warning | Caller-info argument records the wrong call site
TECS0037 | Tralys.Ecs.Scheduling | Error | Playback point is invalid
TECS0038 | Tralys.Ecs.Queries | Error | Query body calls an API that needs a settled world
TECS0039 | Tralys.Ecs.Declarations | Error | Rows type has no row key
TECS0040 | Tralys.Ecs.Declarations | Error | Presence-mask key is out of range
TECS0041 | Tralys.Ecs.Declarations | Error | Type has more than one declaring attribute
TECS0042 | Tralys.Ecs.Declarations | Error | Declaration options conflict or have no effect
TECS0043 | Tralys.Ecs.Queries | Error | Parallel query body uses world scratch
TECS0045 | Tralys.Ecs.Queries | Error | Query body or collect predicate changes structure
TECS0046 | Tralys.Ecs.Usage | Error | Member that is not readonly called through a readonly reference
