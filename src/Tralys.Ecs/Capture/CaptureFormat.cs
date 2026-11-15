using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Tralys.Ecs;

// Values go out as stored, so only a registry that lays every type out the same way may read them back.
internal static class CaptureFormat
{
    const uint Magic = 0x49434C54;
    const ushort Version = 1;

    public static void Write(CaptureImage image, Stream stream)
    {
        if (!BitConverter.IsLittleEndian)
            throw new NotSupportedException("Capture images are written little-endian only.");
        var registry = image.Registry;
        var table = new TypeTable();
        Collect(image, table);
        foreach (var type in table.Types)
        {
            var descriptor = registry[type];
            if (descriptor.Kind != DescriptorKind.Tag && descriptor.Column.Managed)
                throw new NotSupportedException(
                    $"{descriptor.Name} holds managed references, so a capture holding it has no byte form."
                );
        }

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(table.Types.Count);
        foreach (var type in table.Types)
        {
            var descriptor = registry[type];
            writer.Write(descriptor.Name);
            writer.Write(descriptor.SchemaVersion);
            writer.Write(Shape(descriptor, registry));
        }

        writer.Write(image.Sources.Length);
        writer.Write(MemoryMarshal.AsBytes(image.Sources.AsSpan()));
        writer.Write(image.Externals.Length);
        writer.Write(MemoryMarshal.AsBytes(image.Externals.AsSpan()));
        writer.Write(image.Buffers.Length);
        foreach (var buffer in image.Buffers)
        {
            writer.Write(buffer.Length);
            writer.Write(buffer);
        }

        writer.Write(image.Pages.Length);
        foreach (var page in image.Pages)
        {
            writer.Write(page.Detached);
            Ints(writer, page.Locals);
            Types(writer, table, page.Types);
            Types(writer, table, page.ColumnTypes);
            for (var c = 0; c < page.Columns.Length; c++)
                Values(writer, page.Columns[c], registry[page.ColumnTypes[c]].Column.Size);
            Types(writer, table, page.EnableTypes);
            Bools(writer, page.Enabled);
            Bools(writer, page.DetachedSelf);
        }

        writer.Write(image.Sparse.Length);
        foreach (var record in image.Sparse)
        {
            writer.Write(table.Slot(record.Type));
            Ints(writer, record.Locals);
            Values(writer, record.Values, registry[record.Type].Column.Size);
        }

        writer.Write(image.Tags.Length);
        foreach (var record in image.Tags)
        {
            writer.Write(table.Slot(record.Type));
            Ints(writer, record.Locals);
        }

        writer.Write(image.Relations.Length);
        foreach (var record in image.Relations)
        {
            writer.Write(table.Slot(record.Type));
            Ints(writer, record.Sources);
            writer.Write(record.Targets.Length);
            writer.Write(MemoryMarshal.AsBytes(record.Targets.AsSpan()));
            Values(writer, record.Payloads, registry[record.Type].Column.Size);
        }

        writer.Write(image.Parents.Length);
        foreach (var link in image.Parents)
        {
            writer.Write(link.Child);
            writer.Write(link.Parent.Bits);
        }

        writer.Write(image.Rows.Length);
        foreach (var record in image.Rows)
        {
            writer.Write(table.Slot(record.Type));
            Ints(writer, record.Owners);
            Values(writer, record.Values, registry[record.Type].Column.Size);
        }
    }

    public static CaptureImage Read(Stream stream, Registry registry)
    {
        if (!BitConverter.IsLittleEndian)
            throw new NotSupportedException("Capture images are read little-endian only.");
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            stream = copy;
        }
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            return new Reader(reader, registry).Read();
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("The capture image ends early.", error);
        }
        catch (Exception error)
            when (error
                    is ArgumentException
                        or IndexOutOfRangeException
                        or OverflowException
                        or FormatException
            )
        {
            throw new InvalidDataException("The capture image is corrupt.", error);
        }
    }

    // Restore reads a value through its layout, kind, storage and traits, so any of them changing makes old bytes unreadable.
    internal static ulong Shape(ComponentDescriptor descriptor, Registry registry)
    {
        var hash = 14695981039346656037UL;
        void Mix(ulong value)
        {
            for (var i = 0; i < 8; i++, value >>= 8)
                hash = (hash ^ (byte)value) * 1099511628211UL;
        }
        Mix((ulong)descriptor.Kind);
        Mix((ulong)descriptor.Storage);
        Mix((ulong)descriptor.Traits);
        Mix((ulong)descriptor.RelationTraits);
        Mix((ulong)descriptor.Size);
        Mix(descriptor.LayoutHash);
        var requires = new List<string>();
        foreach (var required in descriptor.Requires)
            requires.Add(registry[required].Name);
        requires.Sort(StringComparer.Ordinal);
        foreach (var name in requires)
        {
            Mix((ulong)name.Length);
            foreach (var c in name)
                Mix(c);
        }
        return hash;
    }

    static void Collect(CaptureImage image, TypeTable table)
    {
        foreach (var page in image.Pages)
        {
            foreach (var type in page.Types)
                table.Slot(type);
            foreach (var type in page.ColumnTypes)
                table.Slot(type);
            foreach (var type in page.EnableTypes)
                table.Slot(type);
        }
        foreach (var record in image.Sparse)
            table.Slot(record.Type);
        foreach (var record in image.Tags)
            table.Slot(record.Type);
        foreach (var record in image.Relations)
            table.Slot(record.Type);
        foreach (var record in image.Rows)
            table.Slot(record.Type);
    }

    static void Types(BinaryWriter writer, TypeTable table, int[] types)
    {
        writer.Write(types.Length);
        foreach (var type in types)
            writer.Write(table.Slot(type));
    }

    static void Ints(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    static void Bools(BinaryWriter writer, bool[] values)
    {
        writer.Write(values.Length);
        writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    static void Values(BinaryWriter writer, Array values, int size)
    {
        writer.Write(values.Length);
        writer.Write(
            MemoryMarshal.CreateReadOnlySpan(ref RowBytes.At(values, 0, size), values.Length * size)
        );
    }

    sealed class TypeTable
    {
        readonly Dictionary<int, int> slots = [];
        public readonly List<int> Types = [];

        public int Slot(int type)
        {
            if (!slots.TryGetValue(type, out var slot))
            {
                slot = Types.Count;
                slots[type] = slot;
                Types.Add(type);
            }
            return slot;
        }
    }

    sealed class Reader(BinaryReader reader, Registry registry)
    {
        const int MaxName = 4096;

        int[] types = [];
        int entityCount;

        long Remaining => reader.BaseStream.Length - reader.BaseStream.Position;

        public CaptureImage Read()
        {
            if (reader.ReadUInt32() != Magic)
                throw new InvalidDataException("The stream does not hold a capture image.");
            if (reader.ReadUInt16() is var version and not Version)
                throw new InvalidDataException(
                    $"The capture image is format version {version}; this build reads version {Version}."
                );
            types = new int[Count()];
            for (var t = 0; t < types.Length; t++)
                types[t] = Resolve(Name(), reader.ReadInt32(), reader.ReadUInt64());

            var sources = Array<Entity>();
            entityCount = sources.Length;
            var externals = Array<ExternalRef>();
            var buffers = new byte[Count()][];
            for (var b = 0; b < buffers.Length; b++)
                buffers[b] = Array<byte>();

            var pages = new CapturePage[Count()];
            for (var p = 0; p < pages.Length; p++)
            {
                var detached = reader.ReadBoolean();
                var locals = Locals();
                var pageTypes = TypeList();
                var columnTypes = TypeList();
                var columns = new Array[columnTypes.Length];
                for (var c = 0; c < columns.Length; c++)
                    columns[c] = Values(columnTypes[c], locals.Length);
                var enableTypes = TypeList();
                var enabled = Array<bool>();
                if (enabled.Length != (long)locals.Length * enableTypes.Length)
                    throw Corrupt();
                pages[p] = new CapturePage(
                    detached,
                    pageTypes,
                    columnTypes,
                    columns,
                    enableTypes,
                    enabled,
                    locals,
                    Array<bool>()
                );
            }

            var sparse = new SparseRecord[Count()];
            for (var s = 0; s < sparse.Length; s++)
            {
                var type = TypeAt();
                var locals = Locals();
                sparse[s] = new SparseRecord(type, locals, Values(type, locals.Length));
            }

            var tags = new TagRecord[Count()];
            for (var t = 0; t < tags.Length; t++)
                tags[t] = new TagRecord(TypeAt(), Locals());

            var relations = new RelationRecord[Count()];
            for (var r = 0; r < relations.Length; r++)
            {
                var type = TypeAt();
                var from = Locals();
                var targets = Array<Entity>();
                if (targets.Length != from.Length)
                    throw Corrupt();
                relations[r] = new RelationRecord(type, from, targets, Values(type, from.Length));
            }

            var parents = new ParentLink[Count()];
            for (var l = 0; l < parents.Length; l++)
                parents[l] = new ParentLink(Local(), Entity.Unchecked(reader.ReadUInt64()));

            var rows = new RowRecord[Count()];
            for (var r = 0; r < rows.Length; r++)
            {
                var type = TypeAt();
                var owners = Locals();
                rows[r] = new RowRecord(type, owners, Values(type, owners.Length));
            }

            var image = new CaptureImage(
                registry,
                sources,
                pages,
                sparse,
                tags,
                relations,
                parents,
                rows,
                buffers,
                externals
            );
            new Validation(image, registry).Run();
            return image;
        }

        string Name()
        {
            var length = reader.Read7BitEncodedInt();
            if (length is < 0 or > MaxName)
                throw Corrupt();
            var bytes = new byte[length];
            reader.BaseStream.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        int Resolve(string name, int schemaVersion, ulong shape)
        {
            var descriptor =
                registry.FindByName(name)
                ?? Aliased(name)
                ?? throw new InvalidDataException(
                    $"The capture image holds {name}, which this registry does not declare."
                );
            if (descriptor.SchemaVersion != schemaVersion)
                throw new InvalidDataException(
                    $"The capture image holds {name} at schema version {schemaVersion}; this registry declares version {descriptor.SchemaVersion}."
                );
            if (Shape(descriptor, registry) != shape)
                throw new InvalidDataException(
                    $"{name} changed layout since the capture image was written."
                );
            return descriptor.Index;
        }

        ComponentDescriptor? Aliased(string name)
        {
            foreach (var descriptor in registry.Descriptors)
            {
                foreach (var alias in descriptor.Aliases)
                {
                    if (alias == name)
                        return descriptor;
                }
            }
            return null;
        }

        int Count()
        {
            var count = reader.ReadInt32();
            if (count < 0)
                throw Corrupt();
            if (count > Remaining)
                throw new EndOfStreamException();
            return count;
        }

        T[] Array<T>()
            where T : unmanaged
        {
            var count = Count();
            if ((long)count * Unsafe.SizeOf<T>() > Remaining)
                throw new EndOfStreamException();
            var values = new T[count];
            reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
            return values;
        }

        int TypeAt()
        {
            var slot = reader.ReadInt32();
            return (uint)slot < (uint)types.Length ? types[slot] : throw Corrupt();
        }

        int[] TypeList()
        {
            var list = new int[Count()];
            for (var i = 0; i < list.Length; i++)
                list[i] = TypeAt();
            return list;
        }

        int Local()
        {
            var local = reader.ReadInt32();
            return (uint)local < (uint)entityCount ? local : throw Corrupt();
        }

        int[] Locals()
        {
            var locals = Array<int>();
            foreach (var local in locals)
            {
                if ((uint)local >= (uint)entityCount)
                    throw Corrupt();
            }
            return locals;
        }

        Array Values(int type, int expected)
        {
            if (Count() != expected)
                throw Corrupt();
            var ops = registry[type].Column;
            if ((long)expected * ops.Size > Remaining)
                throw new EndOfStreamException();
            var values = ops.Create(expected);
            reader.BaseStream.ReadExactly(
                MemoryMarshal.CreateSpan(ref RowBytes.At(values, 0, ops.Size), expected * ops.Size)
            );
            return values;
        }

        static InvalidDataException Corrupt() => new("The capture image is corrupt.");
    }

    // Restore trusts a captured image and changes the world as it goes, so bytes that could not come from a capture stop here.
    sealed class Validation(CaptureImage image, Registry registry)
    {
        readonly bool[] placed = new bool[image.Sources.Length];
        readonly Dictionary<int, int> holders = [];
        readonly HashSet<int> recorded = [];

        public void Run()
        {
            foreach (var page in image.Pages)
                Page(page);
            foreach (var done in placed)
                Require(done);
            foreach (var record in image.Sparse)
            {
                Record(record.Type, DescriptorKind.Component, sparse: true, record.Locals);
                Values(record.Type, record.Values);
            }
            foreach (var record in image.Tags)
                Record(record.Type, DescriptorKind.Tag, sparse: true, record.Locals);
            foreach (var record in image.Relations)
            {
                Require(
                    registry[record.Type].Kind == DescriptorKind.Relation
                        && recorded.Add(record.Type)
                );
                foreach (var target in record.Targets)
                    Reference(target);
                Values(record.Type, record.Payloads);
            }
            var children = new HashSet<int>();
            foreach (var link in image.Parents)
            {
                Require(children.Add(link.Child) && !link.Parent.IsDefault);
                Reference(link.Parent);
            }
            foreach (var record in image.Rows)
            {
                Require(
                    registry[record.Type].Kind == DescriptorKind.Rows && recorded.Add(record.Type)
                );
                Values(record.Type, record.Values);
            }
            foreach (var count in holders.Values)
                Require(count <= 1);
        }

        void Page(CapturePage page)
        {
            Require(page.DetachedSelf.Length == (page.Detached ? page.Locals.Length : 0));
            var types = new HashSet<int>();
            var columns = new HashSet<int>();
            var enables = new HashSet<int>();
            foreach (var type in page.Types)
            {
                var descriptor = registry[type];
                Require(
                    !descriptor.IsSparse
                        && descriptor.Kind is DescriptorKind.Component or DescriptorKind.Tag
                        && types.Add(type)
                );
                if (descriptor.HasColumn)
                    columns.Add(type);
                if (descriptor.IsEnableable)
                    enables.Add(type);
                Hold(type, page.Locals.Length);
            }
            Require(
                columns.SetEquals(page.ColumnTypes) && columns.Count == page.ColumnTypes.Length
            );
            Require(
                enables.SetEquals(page.EnableTypes) && enables.Count == page.EnableTypes.Length
            );
            foreach (var local in page.Locals)
            {
                Require(!placed[local]);
                placed[local] = true;
            }
            for (var c = 0; c < page.Columns.Length; c++)
                Values(page.ColumnTypes[c], page.Columns[c]);
        }

        void Record(int type, DescriptorKind kind, bool sparse, int[] locals)
        {
            var descriptor = registry[type];
            Require(descriptor.Kind == kind && descriptor.IsSparse == sparse && recorded.Add(type));
            var seen = new HashSet<int>();
            foreach (var local in locals)
                Require(seen.Add(local));
            Hold(type, locals.Length);
        }

        void Hold(int type, int count)
        {
            if ((registry[type].Traits & ComponentTraits.Singleton) != 0)
                holders[type] = holders.GetValueOrDefault(type) + count;
        }

        void Values(int type, Array values)
        {
            var descriptor = registry[type];
            var plan = CapturePlan.For(descriptor);
            var size = descriptor.Column.Size;
            for (var row = 0; row < values.Length; row++)
            {
                ref var value = ref RowBytes.At(values, row, size);
                foreach (var offset in plan.Refs)
                    Reference(ImageRef.At(ref value, offset));
                for (var i = 0; i < plan.Buffers.Length; i++)
                {
                    var buffer = ImageRef.BufferAt(ref value, plan.Buffers[i]);
                    if (buffer < 0)
                        continue;
                    Require(buffer < image.Buffers.Length);
                    var element = descriptor.BufferOps[plan.BufferOps[i]].ElementSize;
                    Require(image.Buffers[buffer].Length % element == 0);
                    if (plan.EntityBuffers[i])
                    {
                        foreach (var held in ImageRef.Entities(image.Buffers[buffer]))
                            Reference(held);
                    }
                }
            }
        }

        void Reference(Entity encoded)
        {
            if (encoded.IsDefault)
                return;
            Require(encoded.Generation == 0);
            if (ImageRef.TryLocal(encoded, out var local))
                Require((uint)local < (uint)image.Sources.Length);
            else
                Require(ImageRef.ExternalIndex(encoded) < image.Externals.Length);
        }

        static void Require(bool holds)
        {
            if (!holds)
                throw new InvalidDataException("The capture image is corrupt.");
        }
    }
}
