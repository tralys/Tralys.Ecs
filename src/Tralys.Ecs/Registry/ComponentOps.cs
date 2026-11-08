using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Tralys.Ecs;

internal static class ComponentType<T>
    where T : struct
{
    internal static int Index = -1;
}

internal readonly unsafe struct ComponentOps(
    delegate* <int, void> bind,
    delegate* <int> getIndex,
    delegate* <ref byte, ref byte, bool> equals,
    delegate* <int, SparseStorage> createSparse,
    delegate* <int, ComponentDescriptor, RelationStore> createRelation
)
{
    public readonly delegate* <int, SparseStorage> CreateSparse = createSparse;
    public readonly delegate* <int, ComponentDescriptor, RelationStore> CreateRelation =
        createRelation;

    public readonly delegate* <int, void> Bind = bind;
    public readonly delegate* <int> GetIndex = getIndex;

    // Always false for managed types: a shallow compare cannot see a mutation inside a referenced object.
    public readonly delegate* <ref byte, ref byte, bool> ValuesEqual = equals;

    public static ComponentOps For<T>()
        where T : struct =>
        new(
            &ComponentOpsOf<T>.Bind,
            &ComponentOpsOf<T>.GetIndex,
            &ComponentOpsOf<T>.Equals,
            &ComponentOpsOf<T>.CreateSparse,
            &ComponentOpsOf<T>.CreateRelation
        );
}

internal static class ComponentOpsOf<T>
    where T : struct
{
    public static void Bind(int index) => ComponentType<T>.Index = index;

    public static int GetIndex() => ComponentType<T>.Index;

    public static SparseStorage CreateSparse(int type) => new SparseStorage<T>(type);

    public static RelationStore CreateRelation(int type, ComponentDescriptor descriptor) =>
        new RelationStore<T>(type, descriptor);

    public static bool Equals(ref byte a, ref byte b) =>
        !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
        && MemoryMarshal
            .CreateReadOnlySpan(ref a, Unsafe.SizeOf<T>())
            .SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref b, Unsafe.SizeOf<T>()));
}

internal abstract class ColumnOps(int size, bool managed)
{
    public readonly int Size = size;
    public readonly bool Managed = managed;

    public abstract Array Create(int capacity);

    public abstract Array Resize(Array column, int capacity);

    public abstract void CopyManaged(
        Array source,
        int sourceRow,
        Array destination,
        int destinationRow
    );

    public abstract void MoveManaged(
        Array source,
        int sourceRow,
        Array destination,
        int destinationRow
    );

    public abstract void ClearManaged(Array column, int row);

    public abstract void Store(Array destination, int row, ref byte value);

    public abstract void Write(World world, Entity entity, int type, ReadOnlySpan<byte> value);

    public abstract void Visit<TVisitor>(
        ComponentDescriptor descriptor,
        ref byte value,
        ref TVisitor visitor
    )
        where TVisitor : struct, IComponentVisitor;

    public abstract void VisitDefault<TVisitor>(
        ComponentDescriptor descriptor,
        ref TVisitor visitor
    )
        where TVisitor : struct, IComponentVisitor;

    public abstract bool ManagedEqual(Array left, int leftRow, Array right, int rightRow);
}

internal sealed class ColumnOps<T>()
    : ColumnOps(Unsafe.SizeOf<T>(), RuntimeHelpers.IsReferenceOrContainsReferences<T>())
    where T : struct
{
    public static readonly ColumnOps<T> Instance = new();

    public override Array Create(int capacity) =>
        capacity == 0 ? Array.Empty<T>() : new T[capacity];

    public override Array Resize(Array column, int capacity)
    {
        var values = Unsafe.As<T[]>(column);
        Array.Resize(ref values, capacity);
        return values;
    }

    public override void CopyManaged(
        Array source,
        int sourceRow,
        Array destination,
        int destinationRow
    ) => Unsafe.As<T[]>(destination)[destinationRow] = Unsafe.As<T[]>(source)[sourceRow];

    public override void MoveManaged(
        Array source,
        int sourceRow,
        Array destination,
        int destinationRow
    )
    {
        var values = Unsafe.As<T[]>(source);
        Unsafe.As<T[]>(destination)[destinationRow] = values[sourceRow];
        values[sourceRow] = default;
    }

    public override void ClearManaged(Array column, int row) =>
        Unsafe.As<T[]>(column)[row] = default;

    public override void Store(Array destination, int row, ref byte value) =>
        Unsafe.As<T[]>(destination)[row] = Unsafe.As<byte, T>(ref value);

    public override void Write(World world, Entity entity, int type, ReadOnlySpan<byte> value) =>
        world.Write(entity, type, value.IsEmpty ? default : MemoryMarshal.Read<T>(value));

    public override void Visit<TVisitor>(
        ComponentDescriptor descriptor,
        ref byte value,
        ref TVisitor visitor
    ) => visitor.Visit(descriptor, in Unsafe.As<byte, T>(ref value));

    public override void VisitDefault<TVisitor>(
        ComponentDescriptor descriptor,
        ref TVisitor visitor
    ) => visitor.Visit(descriptor, default(T));

    public override bool ManagedEqual(Array left, int leftRow, Array right, int rightRow) =>
        EqualityComparer<T>.Default.Equals(
            Unsafe.As<T[]>(left)[leftRow],
            Unsafe.As<T[]>(right)[rightRow]
        );
}

internal static class RowBytes
{
    // Every single-dimension array keeps its elements at the same offset, so any T[] can be addressed as bytes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref byte At(Array column, int row, int size) =>
        ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(Unsafe.As<byte[]>(column)),
            (nint)row * size
        );

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy(
        Array source,
        int sourceRow,
        Array destination,
        int destinationRow,
        int size
    )
    {
        ref var to = ref At(destination, destinationRow, size);
        ref var from = ref At(source, sourceRow, size);
        switch (size)
        {
            case 4:
                Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<uint>(ref from));
                return;
            case 8:
                Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<ulong>(ref from));
                return;
            case 12:
                Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<ulong>(ref from));
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref to, 8),
                    Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref from, 8))
                );
                return;
            case 16:
                Vector128.LoadUnsafe(ref from).StoreUnsafe(ref to);
                return;
            case 32:
                Vector256.LoadUnsafe(ref from).StoreUnsafe(ref to);
                return;
            default:
                Unsafe.CopyBlockUnaligned(ref to, ref from, (uint)size);
                return;
        }
    }
}
