namespace Tralys.Ecs;

/// <summary>What a capture leaves out. Each flag turns off one part of a capture, so the default, <see cref="None"/>, takes everything: the
/// closure follows children, relation targets and entity fields, and rows and buffer contents are captured.</summary>
[Flags]
public enum CaptureExclusions
{
    /// <summary>Excludes nothing.</summary>
    None = 0,

    /// <summary>Does not follow hierarchy children.</summary>
    Children = 1 << 0,

    /// <summary>Does not follow the targets of the subset's outgoing relations; edges to them become external.</summary>
    Relations = 1 << 1,

    /// <summary>Does not follow entities named by entity fields of captured components, rows and relation payloads, or held in their
    /// entity buffers.</summary>
    EntityFields = 1 << 2,

    /// <summary>Leaves the subset's keyed rows out.</summary>
    Rows = 1 << 3,

    /// <summary>Leaves the contents of owned buffers out, so the closure does not follow entity buffers; buffer-handle fields restore as
    /// null.</summary>
    Buffers = 1 << 4,
}

/// <summary>How a capture grows from its roots and how references leaving the subset are written. The default takes the roots' whole
/// closure, with rows and buffer contents.</summary>
public readonly record struct CaptureOptions
{
    /// <summary>The parts of the capture to leave out; the default leaves out nothing.</summary>
    public CaptureExclusions Exclude { get; init; }

    /// <summary>Components, rows and relations outside these categories are left out. The default takes all.</summary>
    public CategoryMask Categories { get; init; }

    /// <summary>Stops the closure: a candidate it rejects stays outside, and references to it become external.</summary>
    public ICaptureBoundary? Boundary { get; init; }

    /// <summary>Names entities outside the subset. Without one, an external reference carries the source handle's raw value.</summary>
    public IExternalRefTranslator? Translator { get; init; }
}

/// <summary>Decides which entities the capture closure may pull in beyond its roots.</summary>
public interface ICaptureBoundary
{
    /// <summary>True takes <paramref name="candidate"/> into the subset; false leaves it outside, and references to it become external.</summary>
    [Effect(Effects.None)]
    bool Include(World world, Entity candidate);
}

/// <summary>Names the entities a capture's references reach outside the subset, in terms a later restore's resolver understands.</summary>
public interface IExternalRefTranslator
{
    /// <summary>The external reference for <paramref name="outside"/>; called once per distinct outside entity in a capture.</summary>
    [Effect(Effects.None)]
    ExternalRef Translate(World world, Entity outside);
}

/// <summary>Maps an image's external references to entities of the world being restored into.</summary>
public interface IExternalRefResolver
{
    /// <summary>The entity <paramref name="reference"/> names; false, or an entity that does not exist, leaves it unresolved.</summary>
    [Effect(Effects.None)]
    bool TryResolve(in ExternalRef reference, out Entity entity);
}

/// <summary>Adjusts a <see cref="CapturePolicy.Rebase"/> field as it is restored, such as a time measured on another clock.</summary>
public interface IFieldRebaser
{
    /// <summary>Rewrites one restored value of <paramref name="field"/> in place; <paramref name="value"/> is that field's bytes.</summary>
    [Effect(Effects.None)]
    void Rebase(FieldDescriptor field, Span<byte> value);
}

/// <summary>A reference to an entity outside a captured subset, in whatever terms the translator chose.</summary>
public readonly record struct ExternalRef(ulong Value);

/// <summary>What a restore does with an external reference its resolver cannot resolve.</summary>
public enum UnresolvedPolicy : byte
{
    /// <summary>Writes null; a relation edge or parent link to it is dropped.</summary>
    Null,

    /// <summary>As <see cref="Null"/>, and lists the reference in <see cref="RestoreResult.Deferred"/> so the caller can fix it later.</summary>
    Defer,

    /// <summary>Throws before the world is changed.</summary>
    Fail,
}

/// <param name="Resolver">Maps external references to entities of the target world; without one, none resolve.</param>
/// <param name="Unresolved">What to do with an external reference that does not resolve.</param>
/// <param name="Reason">Logged and passed to observers; <see cref="LifecycleReason.None"/> means <see cref="LifecycleReason.Restored"/>.</param>
/// <param name="Rebaser">Adjusts <see cref="CapturePolicy.Rebase"/> fields; without one they restore as captured.</param>
public readonly record struct RestoreOptions(
    IExternalRefResolver? Resolver = null,
    UnresolvedPolicy Unresolved = UnresolvedPolicy.Null,
    LifecycleReason Reason = default,
    IFieldRebaser? Rebaser = null
);

/// <summary>An external reference a restore could not resolve and wrote as null.</summary>
/// <param name="Owner">The restored entity holding the reference.</param>
/// <param name="Type">The component, row or relation type holding it; -1 for a parent link.</param>
/// <param name="Row">For a row or relation type, the position of the row or edge among the owner's of that type in the image; otherwise
/// -1. A row whose key the reference was is not restored.</param>
/// <param name="Offset">The byte offset of the field in the value, the buffer-handle field for an element of an entity buffer; -1 for a
/// relation target or parent link.</param>
/// <param name="Element">For an element of a <c>BufferHandle&lt;Entity&gt;</c>, its index in the buffer; otherwise -1.</param>
/// <param name="Reference">The reference as the image holds it.</param>
public readonly record struct DeferredRef(
    Entity Owner,
    int Type,
    int Row,
    int Offset,
    int Element,
    ExternalRef Reference
);

/// <summary>The outcome of <see cref="World.Restore"/>. Valid until the next restore from the same image.</summary>
public readonly ref struct RestoreResult
{
    readonly CaptureImage image;
    readonly Entity[] created;

    internal RestoreResult(
        CaptureImage image,
        Entity[] created,
        int unresolved,
        DeferredRef[] deferred,
        IndexConflictException? conflict
    )
    {
        this.image = image;
        this.created = created;
        Unresolved = unresolved;
        Deferred = deferred;
        Conflict = conflict;
    }

    /// <summary>The new entities, in the image's order.</summary>
    public ReadOnlySpan<Entity> Created => created;

    /// <summary>External references that did not resolve.</summary>
    public int Unresolved { get; }

    /// <summary>The references that did not resolve, listed only under <see cref="UnresolvedPolicy.Defer"/>; empty otherwise.</summary>
    public ReadOnlySpan<DeferredRef> Deferred { get; }

    /// <summary>The conflicts a <see cref="ConflictPolicy.Throw"/> index saw from restored keys, or null. Restore reports them here
    /// instead of throwing, so the mapping is not lost.</summary>
    public IndexConflictException? Conflict { get; }

    /// <summary>The entity restored from <paramref name="source"/>, or null when the image does not hold it.</summary>
    public Entity Map(Entity source) =>
        image.LocalOf(source) is var local and >= 0 ? created[local] : default;
}

/// <summary>The outcome of <see cref="World.Extract"/>.</summary>
public readonly struct ExtractResult
{
    internal ExtractResult(
        CaptureImage image,
        IndexConflictException? conflict,
        AggregateException? observerFailures
    )
    {
        Image = image;
        Conflict = conflict;
        ObserverFailures = observerFailures;
    }

    /// <summary>The captured subset, which has left the world.</summary>
    public CaptureImage Image { get; }

    /// <summary>The conflicts a <see cref="ConflictPolicy.Throw"/> index saw while the subset left, such as from a destroy observer's
    /// write, or null. Extract reports them here instead of throwing, so the image is not lost.</summary>
    public IndexConflictException? Conflict { get; }

    /// <summary>Every exception a destroy observer threw while the subset left, or null. The subset left regardless, and Extract reports
    /// them here instead of throwing, so the image is not lost.</summary>
    public AggregateException? ObserverFailures { get; }
}

/// <summary>Resolves the external references a capture writes without a translator, the raw source handles, back to the same entities. For
/// restoring an image into the world it was captured from; a handle whose entity has died since does not resolve. After
/// <see cref="World.Reset"/>, which restores generations, a handle can name a different entity that reused it.</summary>
public sealed class SourceWorldResolver(World world) : IExternalRefResolver
{
    /// <inheritdoc/>
    public bool TryResolve(in ExternalRef reference, out Entity entity)
    {
        // Not FromBits: a value no real handle has must resolve to false, not throw.
        entity = Entity.Unchecked(reference.Value);
        if (world.Exists(entity))
            return true;
        entity = default;
        return false;
    }
}
