namespace Tralys.Ecs;

internal enum TermKind : byte
{
    Required,
    Optional,
    With,
    Without,
    AnyOf,
    Changed,
}

internal readonly struct Term(ComponentKey key, TermKind kind, bool write, int group)
{
    public readonly ComponentKey Key = key;
    public readonly TermKind Kind = kind;
    public readonly bool Write = write;
    public readonly int Group = group;

    public bool Positive => Kind is TermKind.Required or TermKind.With or TermKind.Changed;
}

/// <summary>What a query matches. Built once and shared by every world; each world keeps its own <see cref="CachedQuery"/> for it.</summary>
public sealed class QueryDesc
{
    static int nextId;

    internal readonly Term[] Terms;
    internal readonly bool IncludePending;
    internal readonly bool IncludeDetached;

    internal QueryDesc(Term[] terms, bool includePending, bool includeDetached)
    {
        Terms = terms;
        IncludePending = includePending;
        IncludeDetached = includeDetached;
        Id = Interlocked.Increment(ref nextId) - 1;
    }

    /// <summary>A builder for a new description.</summary>
    public static QueryDescBuilder Builder() => new();

    /// <summary>Process-wide id; worlds index their cached queries by it.</summary>
    public int Id { get; }

    /// <summary>The number of terms, value and filter terms alike.</summary>
    public int TermCount => Terms.Length;
}

/// <summary>Builds a <see cref="QueryDesc"/>. Value terms are numbered in the order they are added; chunk views address them by that number.</summary>
public sealed class QueryDescBuilder
{
    readonly List<Term> terms = [];
    bool includePending;
    bool includeDetached;

    internal QueryDescBuilder() { }

    /// <summary>Requires <typeparamref name="T"/> present and enabled, and reads it as the next value term.</summary>
    public QueryDescBuilder Read<T>()
        where T : struct, IComponent => Add<T>(TermKind.Required, false);

    /// <summary>As <see cref="Read{T}"/>, and declares writes to <typeparamref name="T"/>.</summary>
    public QueryDescBuilder Write<T>()
        where T : struct, IMutableComponent => Add<T>(TermKind.Required, true);

    /// <summary>Reads <typeparamref name="T"/> as the next value term when present and enabled, without requiring it.</summary>
    public QueryDescBuilder Optional<T>()
        where T : struct, IComponent => Add<T>(TermKind.Optional, false);

    /// <summary>As <see cref="Optional{T}()"/>, and declares writes to <typeparamref name="T"/>.</summary>
    public QueryDescBuilder OptionalWrite<T>()
        where T : struct, IMutableComponent => Add<T>(TermKind.Optional, true);

    /// <summary>Requires <typeparamref name="T"/> present and enabled, without reading it.</summary>
    public QueryDescBuilder With<T>()
        where T : struct, IComponent => Add<T>(TermKind.With, false);

    /// <summary>Skips entities holding <typeparamref name="T"/> enabled.</summary>
    public QueryDescBuilder Without<T>()
        where T : struct, IComponent => Add<T>(TermKind.Without, false);

    /// <summary>Matches when at least one member of <paramref name="group"/> is present and enabled.</summary>
    public QueryDescBuilder AnyOf<T>(int group)
        where T : struct, IComponent => Add<T>(TermKind.AnyOf, false, group);

    /// <summary>Matches rows whose <typeparamref name="T"/> changed since this query last ran to completion in the world.</summary>
    public QueryDescBuilder Changed<T>()
        where T : struct, IComponent, ITracked => Add<T>(TermKind.Changed, false);

    /// <summary>Also matches entities whose destroy is pending.</summary>
    public QueryDescBuilder IncludePendingDestroy()
    {
        includePending = true;
        return this;
    }

    /// <summary>Also matches detached entities.</summary>
    public QueryDescBuilder IncludeDetached()
    {
        includeDetached = true;
        return this;
    }

    static readonly Dictionary<string, QueryDesc> Interned = new(StringComparer.Ordinal);

    /// <summary>The description of the terms added so far. Equal descriptions return the same <see cref="QueryDesc"/>, so a world keeps one
    /// cached query for them however often they are built.</summary>
    public QueryDesc Build()
    {
        if (!terms.Exists(t => t.Positive || t.Kind == TermKind.AnyOf))
            throw new InvalidOperationException(
                "A query needs a positive term (a read, write, With, AnyOf or Changed); without one it would match every entity."
            );
        var key =
            string.Join(
                ";",
                terms.Select(t =>
                    $"{(int)t.Kind}:{(t.Write ? 1 : 0)}:{t.Group}:{t.Key.Type.AssemblyQualifiedName}"
                )
            ) + $"|{includePending}|{includeDetached}";
        lock (Interned)
        {
            if (!Interned.TryGetValue(key, out var desc))
                Interned[key] = desc = new QueryDesc([.. terms], includePending, includeDetached);
            return desc;
        }
    }

    QueryDescBuilder Add<T>(TermKind kind, bool write, int group = -1)
        where T : struct, IComponent
    {
        terms.Add(new Term(ComponentKey.Of<T>(), kind, write, group));
        return this;
    }
}
