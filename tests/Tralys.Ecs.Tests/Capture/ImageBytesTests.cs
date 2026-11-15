using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Tralys.Ecs.Downstream.Fixture;

namespace Tralys.Ecs.Tests;

public sealed class ImageBytesTests
{
    static CaptureImage RoundTrip(CaptureImage image)
    {
        using var stream = new MemoryStream();
        image.Write(stream);
        stream.Position = 0;
        return CaptureImage.Read(stream, TestWorlds.Registry);
    }

    [Test]
    public async Task An_image_read_back_from_bytes_restores_as_the_original_does()
    {
        using var source = TestWorlds.Create();
        var root = source.Create();
        source.Set(root, new Position { X = 1 });
        source.Set(root, new Burning { Ticks = 4 });
        source.SetEnabled<Burning>(root, false);
        source.Add<Rare>(root);
        source.Set(root, new Dialog { Page = 6 });
        var stops = source.Buffers<int>().Allocate(2);
        source.Buffers<int>().Get(stops)[1] = 9;
        source.Set(root, new Route { Stops = stops });
        source.Add<Npc>(root);
        var outsider = source.Create();
        source.Set(outsider, new Health { Value = 41 });
        source.Set(root, new Link { Target = outsider });
        var child = source.Create();
        source.Set(child, new Health { Value = 3 });
        source.SetParent(child, root);
        source.Relate(child, root, new Owes { Amount = 5 });
        source.EffectRows(child).Set(new Effect { Kind = 2, Stacks = 3 });
        var parked = source.Create();
        source.Set(parked, new Position { X = 8 });
        source.SetParent(parked, root);
        source.Detach(parked);
        var outside = new Outside();
        var image = source.Capture(
            [root],
            new CaptureOptions { Boundary = outside, Translator = outside }
        );

        var read = RoundTrip(image);
        using var fromImage = TestWorlds.Create();
        using var fromBytes = TestWorlds.Create();
        var expected = fromImage.Restore(image).Map(root);
        var restored = fromBytes.Restore(read).Map(root);
        await Assert.That(read.ExternalRefs.ToArray()).IsEquivalentTo(image.ExternalRefs.ToArray());

        await Assert
            .That(
                CaptureImage.Compare(fromImage.Capture([expected]), fromBytes.Capture([restored]))
            )
            .IsEmpty();
        await Assert
            .That((fromBytes.HasStored<Burning>(restored), fromBytes.Has<Burning>(restored)))
            .IsEqualTo((true, false));
    }

    sealed class Outside : ICaptureBoundary, IExternalRefTranslator
    {
        public bool Include(World world, Entity candidate) => !world.Has<Health>(candidate);

        public ExternalRef Translate(World world, Entity outside) =>
            new((ulong)world.Get<Health>(outside).Value);
    }

    static CaptureImage With(
        CaptureImage image,
        Entity[]? sources = null,
        CapturePage[]? pages = null,
        ParentLink[]? parents = null,
        byte[][]? buffers = null
    ) =>
        new(
            image.Registry,
            sources ?? image.Sources,
            pages ?? image.Pages,
            image.Sparse,
            image.Tags,
            image.Relations,
            parents ?? image.Parents,
            image.Rows,
            buffers ?? image.Buffers,
            image.Externals
        );

    static CapturePage Page(
        CapturePage page,
        int[]? types = null,
        int[]? columnTypes = null,
        Array[]? columns = null,
        bool? detached = null,
        bool[]? detachedSelf = null
    ) =>
        new(
            detached ?? page.Detached,
            types ?? page.Types,
            columnTypes ?? page.ColumnTypes,
            columns ?? page.Columns,
            page.EnableTypes,
            page.Enabled,
            page.Locals,
            detachedSelf ?? page.DetachedSelf
        );

    static CapturePage Edited<T>(CapturePage page, Func<T, T> edit)
        where T : struct
    {
        var at = Array.IndexOf(page.ColumnTypes, TestWorlds.Registry.Get<T>().Index);
        var columns = (Array[])page.Columns.Clone();
        var values = (T[])page.Columns[at].Clone();
        values[0] = edit(values[0]);
        columns[at] = values;
        return Page(page, columns: columns);
    }

    [Test]
    public async Task Read_refuses_an_image_that_restore_could_not_apply()
    {
        using var world = TestWorlds.Create();
        var root = world.Create();
        world.Set(root, new Clock { Tick = 1 });
        var child = world.Create();
        world.Set(child, new Position { X = 2 });
        world.SetParent(child, root);
        world.Set(root, new Link { Target = child });
        world.Set(root, new Route { Stops = world.Buffers<int>().Allocate(2) });
        var image = world.Capture([root]);
        var holder = world.Create();
        world.Set(holder, new Roster { Members = world.Buffers<Entity>().Allocate(1) });
        var roster = world.Capture([holder]);
        var clock = TestWorlds.Registry.Get<Clock>().Index;
        var position = TestWorlds.Registry.Get<Position>().Index;
        var held = Array.FindIndex(image.Pages, p => p.Types.Contains(clock));
        var other = Array.FindIndex(image.Pages, p => p.Types.Contains(position));
        CapturePage[] Swap(int at, CapturePage page)
        {
            var pages = (CapturePage[])image.Pages.Clone();
            pages[at] = page;
            return pages;
        }
        var stray = image.Pages[held];

        CaptureImage[] corrupt =
        [
            With(
                image,
                pages: Swap(
                    held,
                    Page(
                        stray,
                        columnTypes: [.. stray.ColumnTypes.Skip(1)],
                        columns: [.. stray.Columns.Skip(1)]
                    )
                )
            ),
            With(image, sources: [.. image.Sources, new Entity(90, 1)]),
            With(image, pages: [.. image.Pages, image.Pages[other]]),
            With(
                image,
                pages: Swap(held, Edited<Link>(stray, l => l with { Target = ImageRef.Local(99) }))
            ),
            With(image, parents: [new ParentLink(1, ImageRef.Local(99))]),
            With(
                image,
                pages: Swap(
                    held,
                    Edited<Route>(
                        stray,
                        r =>
                        {
                            var route = r;
                            ImageRef.SetBuffer(ref Unsafe.As<Route, byte>(ref route), 0, 9);
                            return route;
                        }
                    )
                )
            ),
            With(image, buffers: [new byte[3]]),
            With(
                image,
                pages: Swap(
                    other,
                    Page(
                        image.Pages[other],
                        types: [.. image.Pages[other].Types, clock],
                        columnTypes: [.. image.Pages[other].ColumnTypes, clock],
                        columns: [.. image.Pages[other].Columns, new Clock[1]]
                    )
                )
            ),
            With(image, pages: Swap(held, Page(stray, detached: true, detachedSelf: []))),
            With(
                roster,
                buffers: [MemoryMarshal.AsBytes(new[] { ImageRef.Local(99) }.AsSpan()).ToArray()]
            ),
        ];

        RoundTrip(image);
        RoundTrip(roster);
        var accepted = new List<int>();
        for (var c = 0; c < corrupt.Length; c++)
        {
            using var stream = new MemoryStream();
            corrupt[c].Write(stream);
            stream.Position = 0;
            try
            {
                CaptureImage.Read(stream, TestWorlds.Registry);
                accepted.Add(c);
            }
            catch (InvalidDataException) { }
        }

        await Assert.That(accepted).IsEmpty();
    }

    [Test]
    public async Task Bytes_whose_type_changed_schema_version_or_layout_are_refused()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Position { X = 1 });
        using var stream = new MemoryStream();
        world.Capture([entity]).Write(stream);
        var bytes = stream.ToArray();
        var name = Encoding.UTF8.GetBytes(TestWorlds.Registry.Get<Position>().Name);
        var version = bytes.AsSpan().IndexOf(name) + name.Length;

        var otherVersion = (byte[])bytes.Clone();
        otherVersion[version]++;
        var otherShape = (byte[])bytes.Clone();
        otherShape[version + sizeof(int)]++;

        await Assert
            .That(() => CaptureImage.Read(new MemoryStream(otherVersion), TestWorlds.Registry))
            .Throws<InvalidDataException>();
        await Assert
            .That(() => CaptureImage.Read(new MemoryStream(otherShape), TestWorlds.Registry))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task An_image_holding_a_managed_component_has_no_byte_form()
    {
        using var world = TestWorlds.Create();
        var entity = world.Create();
        world.Set(entity, new Label { Text = "sign" });

        await Assert
            .That(() => world.Capture([entity]).Write(new MemoryStream()))
            .Throws<NotSupportedException>();
    }
}
