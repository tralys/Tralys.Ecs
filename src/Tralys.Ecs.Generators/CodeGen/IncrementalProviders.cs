using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Tralys.Ecs.Generators.CodeGen;

static class IncrementalProviders
{
    public static IncrementalValuesProvider<T> WhereNotNull<T>(
        this IncrementalValuesProvider<T?> source
    )
        where T : class =>
        source.SelectMany(
            static (item, _) => item is null ? ImmutableArray<T>.Empty : ImmutableArray.Create(item)
        );
}
