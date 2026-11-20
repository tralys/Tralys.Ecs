namespace Tralys.Ecs;

/// <summary>Generates <c>Run{Name}</c>, <c>Run{Name}For(entity)</c>, <c>Run{Name}For(entities)</c>, <c>Run{Name}ForSubtree(root)</c>,
/// <c>Run{Name}ChildrenOf(parent)</c> and <c>Run{Name}TargetsOf&lt;R&gt;(source)</c> for a query body. Parameters:
/// <see cref="Entity"/>, <c>in T</c>, <c>ref T</c>, <see cref="Optional{T}"/>, <see cref="OptionalRef{T}"/>, <see cref="Commands"/> (parallel only),
/// <see cref="ChunkContext"/>, <see cref="World"/>, <see cref="Parent{T}"/>, <see cref="Up{T}"/>, one <c>ref</c> accumulator implementing <see cref="IReduction{TSelf}"/>, and
/// <see cref="ArgAttribute"/> values passed through the run. A <c>bool</c> body stops the run by returning false. A serial body reads anything
/// and writes values at once but changes no structure, which throws; a <see cref="CollectAttribute"/> act makes that change.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class QueryAttribute : Attribute
{
    /// <summary>Run chunks through the world's executor. The body may not change the world directly: it writes its own <c>ref</c> columns
    /// and records structural changes through its <see cref="Commands"/>, which apply at the next playback.</summary>
    public bool Parallel { get; set; }

    /// <summary>Run rows in order of hierarchy depth, parents before children. Serial only, and with no <c>TargetsOf</c> run, since
    /// relation order is not depth order.</summary>
    public bool DepthOrdered { get; set; }
}

/// <summary>Collect-then-act: the body is a predicate that, like a serial query body, changes no structure; the run snapshots the entities
/// it returns true for, closes the walk, then calls <see cref="Act"/> for each one still alive, where structural changes apply at once
/// and faults are contained per entity.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class CollectAttribute : Attribute
{
    /// <summary>The method run per collected entity. It takes an <see cref="Entity"/> or <see cref="WorldEntity"/>, optionally a
    /// <see cref="World"/>, and any of the predicate's <see cref="ArgAttribute"/> parameters by name.</summary>
    public required string Act { get; set; }
}

/// <summary>The query matches only entities holding <typeparamref name="T"/> enabled; the body does not read it.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class WithAttribute<T> : Attribute
    where T : struct, IComponent;

/// <summary>The query skips entities holding <typeparamref name="T"/> enabled.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class WithoutAttribute<T> : Attribute
    where T : struct, IComponent;

/// <summary>Matches when at least one member of <see cref="Group"/> is present and enabled.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class AnyOfAttribute<T> : Attribute
    where T : struct, IComponent
{
    /// <summary>The group the term joins; a query matches when each of its groups has a member present and enabled.</summary>
    public int Group { get; set; }
}

/// <summary>Visits only rows whose <typeparamref name="T"/> changed since the query last ran to completion in the world.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ChangedAttribute<T> : Attribute
    where T : struct, IComponent, ITracked;

/// <summary>The query also visits entities whose destroy is pending; by default it skips them.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class IncludePendingDestroyAttribute : Attribute;

/// <summary>The query also visits detached entities; by default it skips them.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class IncludeDetachedAttribute : Attribute;

/// <summary>A query parameter passed through the generated run methods instead of read from the entity.</summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class ArgAttribute : Attribute;
