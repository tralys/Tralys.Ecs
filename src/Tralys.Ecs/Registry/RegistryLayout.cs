namespace Tralys.Ecs;

internal enum StorageClass : byte
{
    None,
    Column,
    SignatureTag,
    Sparse,
    SparseTag,
}

// Shared by every world built from the registry, so no world holds a table sized by the registry.
internal sealed class RegistryLayout
{
    public readonly StorageClass[] Class;
    public readonly bool[] ReplaceOnly;
    public readonly bool[] Enableable;
    public readonly int[] SparseSlot;
    public readonly int[] SparseTagSlot;
    public readonly int[] SingletonSlot;
    public readonly int SparseCount;
    public readonly int SparseTagCount;
    public readonly int SingletonCount;

    // Slot 0 is pending destroy; each inherited enableable tag gets the next slot.
    public readonly int[] InheritedSlot;
    public readonly int[] InheritedTypes;
    public readonly int InheritedCount;

    public readonly bool[] PropagatesToAncestors;
    public readonly bool[] PropagatesToSources;
    public readonly bool[] OwnsBuffers;
    public readonly int[] RelationSlot;
    public readonly int RelationCount;
    public readonly int[] RowSlot;
    public readonly int RowCount;

    public readonly int[] IndexBase;
    public readonly int[] IndexSlotType;
    public readonly int[] IndexedTypes;
    public readonly int[] SparseIndexedTypes;
    public readonly int[] PerParentSlots;

    public RegistryLayout(Registry registry)
    {
        var count = registry.IndexBound;
        Class = new StorageClass[count];
        ReplaceOnly = new bool[count];
        Enableable = new bool[count];
        SparseSlot = new int[count];
        SparseTagSlot = new int[count];
        SingletonSlot = new int[count];
        InheritedSlot = new int[count];
        PropagatesToAncestors = new bool[count];
        PropagatesToSources = new bool[count];
        OwnsBuffers = new bool[count];
        RelationSlot = new int[count];
        RowSlot = new int[count];
        Array.Fill(SparseSlot, -1);
        Array.Fill(SparseTagSlot, -1);
        Array.Fill(SingletonSlot, -1);
        Array.Fill(InheritedSlot, -1);
        Array.Fill(RelationSlot, -1);
        Array.Fill(RowSlot, -1);
        var inherited = new List<int> { -1 };
        foreach (var descriptor in registry.Descriptors)
        {
            var index = descriptor.Index;
            PropagatesToAncestors[index] =
                (descriptor.Traits & ComponentTraits.PropagateToAncestors) != 0;
            PropagatesToSources[index] =
                (descriptor.Traits & ComponentTraits.PropagateToRelationSources) != 0;
            OwnsBuffers[index] = descriptor.BufferHandleOffsets.Length > 0;
            switch (descriptor.Kind)
            {
                case DescriptorKind.Relation:
                    RelationSlot[index] = RelationCount++;
                    continue;
                case DescriptorKind.Rows:
                    RowSlot[index] = RowCount++;
                    continue;
            }
            if (
                descriptor.IsTag
                && descriptor.IsEnableable
                && (descriptor.Traits & ComponentTraits.InheritedTag) != 0
            )
            {
                InheritedSlot[index] = inherited.Count;
                inherited.Add(index);
            }
            if (descriptor.IsTag)
            {
                if (descriptor.IsSparse)
                {
                    Class[index] = StorageClass.SparseTag;
                    SparseTagSlot[index] = SparseTagCount++;
                }
                else
                    Class[index] = StorageClass.SignatureTag;
            }
            else if (descriptor.IsSparse)
            {
                Class[index] = StorageClass.Sparse;
                SparseSlot[index] = SparseCount++;
            }
            else
                Class[index] = StorageClass.Column;
            if ((descriptor.Traits & ComponentTraits.Singleton) != 0)
                SingletonSlot[index] = SingletonCount++;
            ReplaceOnly[index] = (descriptor.Traits & ComponentTraits.ReplaceOnly) != 0;
            Enableable[index] = descriptor.IsEnableable;
        }
        InheritedTypes = [.. inherited];
        InheritedCount = inherited.Count;

        IndexBase = new int[count];
        Array.Fill(IndexBase, -1);
        var slotTypes = new List<int>();
        var indexed = new List<int>();
        var sparseIndexed = new List<int>();
        var perParent = new List<int>();
        foreach (var descriptor in registry.Descriptors)
        {
            var declarations = descriptor.Indexes;
            if (declarations.Count == 0)
                continue;
            if (Class[descriptor.Index] is not (StorageClass.Column or StorageClass.Sparse))
                throw new NotSupportedException(
                    $"{descriptor.Name}: only components with a value can be indexed."
                );
            IndexBase[descriptor.Index] = slotTypes.Count;
            indexed.Add(descriptor.Index);
            if (Class[descriptor.Index] == StorageClass.Sparse)
                sparseIndexed.Add(descriptor.Index);
            foreach (var declaration in declarations)
            {
                if (declaration.PerParent)
                    perParent.Add(slotTypes.Count);
                slotTypes.Add(descriptor.Index);
            }
        }
        IndexSlotType = [.. slotTypes];
        IndexedTypes = [.. indexed];
        SparseIndexedTypes = [.. sparseIndexed];
        PerParentSlots = [.. perParent];
        if (SparseCount + SparseTagCount > ushort.MaxValue)
            throw new NotSupportedException(
                $"A registry holds at most {ushort.MaxValue} sparse component and tag types."
            );
    }
}
