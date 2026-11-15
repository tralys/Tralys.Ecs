using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

internal sealed class CaptureComparer(CaptureImage leftImage, CaptureImage rightImage)
{
    readonly Side left = new(leftImage);
    readonly Side right = new(rightImage);
    readonly Registry registry = leftImage.Registry;
    readonly List<WorldDifference> found = [];
    int local;

    readonly record struct Value(Array Values, int Row);

    sealed class Side
    {
        public readonly CaptureImage Image;
        public readonly CapturePage?[] Pages;
        public readonly int[] Rows;
        public readonly List<(int Type, Value Value)>[] Sparse;
        public readonly List<int>[] Tags;
        public readonly List<(int Type, Entity Target, Value Payload)>[] Edges;
        public readonly Entity[] Parents;
        public readonly List<int>[] Children;
        public readonly List<(int Type, Value Row)>[] OwnedRows;

        public Side(CaptureImage image)
        {
            Image = image;
            var count = image.EntityCount;
            Pages = new CapturePage?[count];
            Rows = new int[count];
            Sparse = Lists<(int, Value)>(count);
            Tags = Lists<int>(count);
            Edges = Lists<(int, Entity, Value)>(count);
            Parents = new Entity[count];
            Children = Lists<int>(count);
            OwnedRows = Lists<(int, Value)>(count);
            foreach (var page in image.Pages)
            {
                for (var row = 0; row < page.Locals.Length; row++)
                {
                    Pages[page.Locals[row]] = page;
                    Rows[page.Locals[row]] = row;
                }
            }
            foreach (var record in image.Sparse)
            {
                for (var k = 0; k < record.Locals.Length; k++)
                    Sparse[record.Locals[k]].Add((record.Type, new Value(record.Values, k)));
            }
            foreach (var record in image.Tags)
            {
                foreach (var local in record.Locals)
                    Tags[local].Add(record.Type);
            }
            foreach (var record in image.Relations)
            {
                for (var k = 0; k < record.Sources.Length; k++)
                    Edges[record.Sources[k]]
                        .Add((record.Type, record.Targets[k], new Value(record.Payloads, k)));
            }
            foreach (var link in image.Parents)
            {
                Parents[link.Child] = link.Parent;
                if (ImageRef.TryLocal(link.Parent, out var parent))
                    Children[parent].Add(link.Child);
            }
            foreach (var record in image.Rows)
            {
                for (var k = 0; k < record.Owners.Length; k++)
                    OwnedRows[record.Owners[k]].Add((record.Type, new Value(record.Values, k)));
            }
        }

        static List<T>[] Lists<T>(int count)
        {
            var lists = new List<T>[count];
            for (var i = 0; i < count; i++)
                lists[i] = [];
            return lists;
        }

        public Entity Source(int local) =>
            local < Image.EntityCount ? Image.Sources[local] : default;
    }

    public List<WorldDifference> Run()
    {
        var shared = Math.Min(left.Image.EntityCount, right.Image.EntityCount);
        for (local = 0; local < shared; local++)
            Entity();
        for (local = shared; local < left.Image.EntityCount; local++)
            Report(DifferenceKind.EntityOnlyInLeft, null);
        for (local = shared; local < right.Image.EntityCount; local++)
            Report(DifferenceKind.EntityOnlyInRight, null);
        return found;
    }

    void Report(DifferenceKind kind, int? type) =>
        found.Add(
            new WorldDifference(
                kind,
                local,
                left.Source(local),
                right.Source(local),
                type is { } index ? registry[index] : null
            )
        );

    void Entity()
    {
        var a = left.Pages[local];
        var b = right.Pages[local];
        if (
            a?.Detached != b?.Detached
            || a?.DetachedItself(left.Rows[local]) != b?.DetachedItself(right.Rows[local])
        )
            Report(DifferenceKind.PartitionDiffers, null);
        Signature(a, left.Rows[local], b, right.Rows[local]);
        Keyed(left.Sparse[local], right.Sparse[local]);
        Tags(left.Tags[local], right.Tags[local]);
        if (!SameRef(left.Parents[local], left.Image, right.Parents[local], right.Image))
            Report(DifferenceKind.ParentDiffers, null);
        if (!left.Children[local].SequenceEqual(right.Children[local]))
            Report(DifferenceKind.ChildOrderDiffers, null);
        Edges();
        OwnedRows();
    }

    void Signature(CapturePage? a, int rowA, CapturePage? b, int rowB)
    {
        ReadOnlySpan<int> typesA = a is null ? [] : a.Types;
        ReadOnlySpan<int> typesB = b is null ? [] : b.Types;
        foreach (var type in typesA)
        {
            if (!typesB.Contains(type))
                Report(DifferenceKind.ComponentOnlyInLeft, type);
        }
        foreach (var type in typesB)
        {
            if (!typesA.Contains(type))
                Report(DifferenceKind.ComponentOnlyInRight, type);
        }
        if (a is null || b is null)
            return;
        for (var c = 0; c < a.ColumnTypes.Length; c++)
        {
            var other = Array.IndexOf(b.ColumnTypes, a.ColumnTypes[c]);
            if (
                other >= 0
                && !SameValue(
                    a.ColumnTypes[c],
                    new Value(a.Columns[c], rowA),
                    new Value(b.Columns[other], rowB)
                )
            )
                Report(DifferenceKind.ValueDiffers, a.ColumnTypes[c]);
        }
        for (var e = 0; e < a.EnableTypes.Length; e++)
        {
            var other = Array.IndexOf(b.EnableTypes, a.EnableTypes[e]);
            if (
                other >= 0
                && a.Enabled[(rowA * a.EnableTypes.Length) + e]
                    != b.Enabled[(rowB * b.EnableTypes.Length) + other]
            )
                Report(DifferenceKind.EnabledDiffers, a.EnableTypes[e]);
        }
    }

    void Keyed(List<(int Type, Value Value)> a, List<(int Type, Value Value)> b)
    {
        foreach (var (type, value) in a)
        {
            var other = b.FindIndex(x => x.Type == type);
            if (other < 0)
                Report(DifferenceKind.ComponentOnlyInLeft, type);
            else if (!SameValue(type, value, b[other].Value))
                Report(DifferenceKind.ValueDiffers, type);
        }
        foreach (var (type, _) in b)
        {
            if (!a.Exists(x => x.Type == type))
                Report(DifferenceKind.ComponentOnlyInRight, type);
        }
    }

    void Tags(List<int> a, List<int> b)
    {
        foreach (var type in a)
        {
            if (!b.Contains(type))
                Report(DifferenceKind.ComponentOnlyInLeft, type);
        }
        foreach (var type in b)
        {
            if (!a.Contains(type))
                Report(DifferenceKind.ComponentOnlyInRight, type);
        }
    }

    void Edges()
    {
        var a = left.Edges[local];
        var b = right.Edges[local];
        foreach (var type in Types(a.ConvertAll(e => e.Type), b.ConvertAll(e => e.Type)))
        {
            var ofA = a.FindAll(e => e.Type == type);
            var ofB = b.FindAll(e => e.Type == type);
            var same = ofA.Count == ofB.Count;
            for (var i = 0; same && i < ofA.Count; i++)
                same =
                    SameRef(ofA[i].Target, left.Image, ofB[i].Target, right.Image)
                    && SameValue(type, ofA[i].Payload, ofB[i].Payload);
            if (!same)
                Report(DifferenceKind.RelationsDiffer, type);
        }
    }

    void OwnedRows()
    {
        var a = left.OwnedRows[local];
        var b = right.OwnedRows[local];
        foreach (var type in Types(a.ConvertAll(r => r.Type), b.ConvertAll(r => r.Type)))
        {
            var ofA = a.FindAll(r => r.Type == type);
            var ofB = b.FindAll(r => r.Type == type);
            var same = ofA.Count == ofB.Count;
            for (var i = 0; same && i < ofA.Count; i++)
                same = SameValue(type, ofA[i].Row, ofB[i].Row);
            if (!same)
                Report(DifferenceKind.RowsDiffer, type);
        }
    }

    static List<int> Types(List<int> a, List<int> b)
    {
        var types = new List<int>();
        foreach (var type in a.Concat(b))
        {
            if (!types.Contains(type))
                types.Add(type);
        }
        return types;
    }

    bool SameValue(int type, Value a, Value b)
    {
        var descriptor = registry[type];
        var ops = descriptor.Column;
        var plan = CapturePlan.For(descriptor);
        if (ops.Managed)
            return SameManaged(ops, plan, a, b);
        ref var x = ref RowBytes.At(a.Values, a.Row, ops.Size);
        ref var y = ref RowBytes.At(b.Values, b.Row, ops.Size);
        for (var offset = 0; offset < ops.Size; )
        {
            if (plan.Refs.Contains(offset))
            {
                if (
                    !SameRef(
                        ImageRef.At(ref x, offset),
                        left.Image,
                        ImageRef.At(ref y, offset),
                        right.Image
                    )
                )
                    return false;
                offset += Unsafe.SizeOf<Entity>();
                continue;
            }
            if (Array.IndexOf(plan.Buffers, offset) is var buffer and >= 0)
            {
                if (
                    !SameBuffer(
                        ImageRef.BufferAt(ref x, offset),
                        ImageRef.BufferAt(ref y, offset),
                        plan.EntityBuffers[buffer]
                    )
                )
                    return false;
                offset += 2 * sizeof(int);
                continue;
            }
            if (Unsafe.Add(ref x, offset) != Unsafe.Add(ref y, offset))
                return false;
            offset++;
        }
        return true;
    }

    // Refs and buffer handles are compared decoded, then blanked in copies so the type's equality sees only the other fields.
    bool SameManaged(ColumnOps ops, CapturePlan plan, Value a, Value b)
    {
        ref var x = ref RowBytes.At(a.Values, a.Row, ops.Size);
        ref var y = ref RowBytes.At(b.Values, b.Row, ops.Size);
        foreach (var offset in plan.Refs)
        {
            if (
                !SameRef(
                    ImageRef.At(ref x, offset),
                    left.Image,
                    ImageRef.At(ref y, offset),
                    right.Image
                )
            )
                return false;
        }
        for (var i = 0; i < plan.Buffers.Length; i++)
        {
            var offset = plan.Buffers[i];
            if (
                !SameBuffer(
                    ImageRef.BufferAt(ref x, offset),
                    ImageRef.BufferAt(ref y, offset),
                    plan.EntityBuffers[i]
                )
            )
                return false;
        }
        var copyA = ops.Create(1);
        var copyB = ops.Create(1);
        ops.Store(copyA, 0, ref x);
        ops.Store(copyB, 0, ref y);
        ref var cx = ref RowBytes.At(copyA, 0, ops.Size);
        ref var cy = ref RowBytes.At(copyB, 0, ops.Size);
        foreach (var offset in plan.Refs)
        {
            ImageRef.At(ref cx, offset) = default;
            ImageRef.At(ref cy, offset) = default;
        }
        foreach (var offset in plan.Buffers)
        {
            ImageRef.ClearBuffer(ref cx, offset);
            ImageRef.ClearBuffer(ref cy, offset);
        }
        return ops.ManagedEqual(copyA, 0, copyB, 0);
    }

    bool SameBuffer(int a, int b, bool entities)
    {
        if (a < 0 || b < 0)
            return a == b;
        if (!entities)
            return left.Image.Buffers[a].AsSpan().SequenceEqual(right.Image.Buffers[b]);
        var x = ImageRef.Entities(left.Image.Buffers[a]);
        var y = ImageRef.Entities(right.Image.Buffers[b]);
        if (x.Length != y.Length)
            return false;
        for (var i = 0; i < x.Length; i++)
        {
            if (!SameRef(x[i], left.Image, y[i], right.Image))
                return false;
        }
        return true;
    }

    static bool SameRef(Entity a, CaptureImage imageA, Entity b, CaptureImage imageB)
    {
        if (a.IsDefault || b.IsDefault)
            return a.IsDefault && b.IsDefault;
        var localA = ImageRef.TryLocal(a, out var la);
        var localB = ImageRef.TryLocal(b, out var lb);
        if (localA || localB)
            return localA && localB && la == lb;
        return imageA.Externals[ImageRef.ExternalIndex(a)]
            == imageB.Externals[ImageRef.ExternalIndex(b)];
    }
}
