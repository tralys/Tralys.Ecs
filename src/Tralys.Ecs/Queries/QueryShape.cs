namespace Tralys.Ecs;

internal sealed class QueryGroup(int[] members, bool hasSparse)
{
    public readonly int[] Members = members;
    public readonly bool HasSparse = hasSparse;
}

// Shared by every world built from one registry: thousands of worlds must not each hold a layout per matched archetype.
internal sealed class QueryShape
{
    public readonly QueryDesc Desc;
    public readonly int[] Types;
    public readonly StorageClass[] Classes;
    public readonly bool[] Enableable;
    public readonly int[] Inherited;
    public readonly QueryGroup[] Groups;
    public readonly int[] SparseRequired;
    public readonly int[] SparseExcluded;
    public readonly int[] SparseChanged;
    public readonly bool RowProbe;
    public readonly bool HasChanged;
    public readonly int Drivers;
    readonly Dictionary<ArchetypeSignature, ArchetypeMatch?> matches = [];
    readonly Lock gate = new();

    public QueryShape(Registry registry, QueryDesc desc)
    {
        Desc = desc;
        var terms = desc.Terms;
        var layout = registry.Layout;
        Types = new int[terms.Length];
        Classes = new StorageClass[terms.Length];
        Enableable = new bool[terms.Length];
        Inherited = new int[terms.Length];
        var required = new List<int>();
        var excluded = new List<int>();
        var changed = new List<int>();
        for (var t = 0; t < terms.Length; t++)
        {
            var type = terms[t].Key.Index;
            if (!registry.Contains(type))
                throw new InvalidOperationException(
                    $"Query term {t} names a component outside this world's registry."
                );
            Types[t] = type;
            var storage = Classes[t] = layout.Class[type];
            var descriptor = registry[type];
            Enableable[t] = descriptor.IsEnableable;
            Inherited[t] = layout.InheritedSlot[type];
            switch (terms[t].Kind)
            {
                case TermKind.Required or TermKind.Optional when descriptor.IsTag:
                    throw new InvalidOperationException(
                        $"{descriptor.Name} is a tag and has no value; match it with With instead."
                    );
                case TermKind.Changed
                    when !descriptor.IsTracked
                        || storage == StorageClass.SparseTag
                        || (storage == StorageClass.SignatureTag && !descriptor.IsEnableable):
                    throw new InvalidOperationException(
                        $"{descriptor.Name} carries no change stamps, so Changed can never match it."
                    );
            }
            if (storage is not (StorageClass.Sparse or StorageClass.SparseTag))
                continue;
            switch (terms[t].Kind)
            {
                case TermKind.Required or TermKind.With:
                    required.Add(t);
                    break;
                case TermKind.Without:
                    excluded.Add(t);
                    break;
                case TermKind.Changed:
                    changed.Add(t);
                    break;
            }
        }
        SparseRequired = [.. required];
        SparseExcluded = [.. excluded];
        SparseChanged = [.. changed];
        var classes = Classes;
        Groups =
        [
            .. Enumerable
                .Range(0, terms.Length)
                .Where(t => terms[t].Kind == TermKind.AnyOf)
                .GroupBy(t => terms[t].Group)
                .OrderBy(g => g.Key)
                .Select(g => new QueryGroup(
                    [.. g],
                    g.Any(t => classes[t] is StorageClass.Sparse or StorageClass.SparseTag)
                )),
        ];
        RowProbe =
            SparseRequired.Length + SparseExcluded.Length + SparseChanged.Length > 0
            || Groups.Any(g => g.HasSparse);
        HasChanged = terms.Any(t => t.Kind == TermKind.Changed);
        Drivers = SparseRequired.Count(t => classes[t] == StorageClass.Sparse);
    }

    public ArchetypeMatch? Match(Archetype archetype)
    {
        lock (gate)
        {
            if (!matches.TryGetValue(archetype.Signature, out var match))
            {
                match = Matches(archetype.Signature) ? new ArchetypeMatch(archetype, this) : null;
                matches.Add(archetype.Signature, match);
            }
            return match;
        }
    }

    bool Matches(in ArchetypeSignature signature)
    {
        if (signature.Detached && !Desc.IncludeDetached)
            return false;
        var terms = Desc.Terms;
        for (var t = 0; t < terms.Length; t++)
        {
            if (Classes[t] is not (StorageClass.Column or StorageClass.SignatureTag))
                continue;
            switch (terms[t].Kind)
            {
                case TermKind.Required
                or TermKind.With
                or TermKind.Changed when !signature.Has(Types[t]) && Inherited[t] <= 0:
                    return false;
                case TermKind.Without when signature.Has(Types[t]) && !Enableable[t]:
                    return false;
            }
        }
        foreach (var group in Groups)
        {
            if (group.HasSparse)
                continue;
            var any = false;
            foreach (var member in group.Members)
                any |= signature.Has(Types[member]) || Inherited[member] > 0;
            if (!any)
                return false;
        }
        return true;
    }
}

// A row bit drawn from an enable column, an inherited-ancestor column, or both; Always is set for a present term without enable bits.
internal readonly struct WordSource(int enable, int inherited, bool always)
{
    public readonly int Enable = enable;
    public readonly int Inherited = inherited;
    public readonly bool Always = always;
}

internal sealed class ArchetypeMatch
{
    public readonly short[] Column;
    public readonly short[] Enable;
    public readonly short[] Tracked;
    public readonly bool[] Present;
    public readonly WordSource[] Required;
    public readonly WordSource[] Excluded;
    public readonly int[] ChangedTracked;
    public readonly WordSource[][] WordGroups;

    public ArchetypeMatch(Archetype archetype, QueryShape shape)
    {
        var terms = shape.Desc.Terms;
        Column = new short[terms.Length];
        Enable = new short[terms.Length];
        Tracked = new short[terms.Length];
        Present = new bool[terms.Length];
        var required = new List<WordSource>();
        var excluded = new List<WordSource>();
        var changed = new List<int>();
        for (var t = 0; t < terms.Length; t++)
        {
            Column[t] = Enable[t] = Tracked[t] = -1;
            var at = archetype.IndexOf(shape.Types[t]);
            if (at >= 0)
            {
                ref readonly var slot = ref archetype.SlotAt(at);
                Present[t] = true;
                Column[t] = slot.Column;
                Enable[t] = slot.Enable;
                Tracked[t] = slot.Tracked;
                if (terms[t].Kind == TermKind.Changed)
                    changed.Add(slot.Tracked);
            }
            if (Source(t, shape) is not { } source)
                continue;
            switch (terms[t].Kind)
            {
                case TermKind.Required or TermKind.With or TermKind.Changed when !source.Always:
                    required.Add(source);
                    break;
                case TermKind.Without:
                    excluded.Add(source);
                    break;
            }
        }
        Required = [.. required];
        Excluded = [.. excluded];
        ChangedTracked = [.. changed];
        WordGroups =
        [
            .. shape
                .Groups.Where(g => !g.HasSparse)
                .Select(g =>
                    g.Members.Select(m => Source(m, shape))
                        .Where(s => s is not null)
                        .Select(s => s!.Value)
                        .ToArray()
                ),
        ];
    }

    WordSource? Source(int term, QueryShape shape)
    {
        var inherited = shape.Inherited[term];
        if (Present[term])
            return Enable[term] < 0 && inherited <= 0
                ? new WordSource(-1, -1, always: true)
                : new WordSource(Enable[term], inherited, always: false);
        return inherited > 0 ? new WordSource(-1, inherited, always: false) : null;
    }
}

internal readonly struct Matched(Archetype archetype, ArchetypeMatch match)
{
    public readonly Archetype Archetype = archetype;
    public readonly ArchetypeMatch Match = match;
}
