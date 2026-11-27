; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
TECS0047 | Tralys.Ecs.Declarations | Error | Declared effects differ from what the member can reach
TECS0048 | Tralys.Ecs.Declarations | Error | Code consumers write is reached without a declared effect

### Removed Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
TECS0017 | Tralys.Ecs.Queries | Error | Parallel query body changes the world directly
TECS0038 | Tralys.Ecs.Queries | Error | Query body calls an API that needs a settled world
TECS0043 | Tralys.Ecs.Queries | Error | Parallel query body uses world scratch
TECS0045 | Tralys.Ecs.Queries | Error | Query body or collect predicate changes structure
