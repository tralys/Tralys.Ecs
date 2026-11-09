namespace Tralys.Ecs.Tests;

public sealed class ListArenaTests
{
    [Test]
    public async Task Lists_keep_their_items_in_order_when_emptying_lists_compacts_the_arena()
    {
        var arena = new ListArena<int>();
        var lists = new int[8];
        for (var l = 0; l < lists.Length; l++)
        {
            lists[l] = arena.New();
            for (var i = 0; i < 1024; i++)
                arena.Add(lists[l], l * 10_000 + i);
        }
        var dropped = arena.New();
        arena.Add(dropped, -1);

        for (var l = 0; l < lists.Length; l++)
        {
            for (var i = 0; i < 1000; i++)
                arena.RemoveAt(lists[l], 0);
        }
        arena.Free(dropped);
        for (var i = 0; i < 9; i++)
            arena.Add(lists[0], -i);

        var actual = string.Join(
            " ",
            lists.Select(list => string.Join(",", arena.Span(list).ToArray()))
        );
        var expected = string.Join(
            " ",
            lists.Select(
                (_, l) =>
                    string.Join(
                        ",",
                        Enumerable
                            .Range(1000, 24)
                            .Select(i => l * 10_000 + i)
                            .Concat(l == 0 ? Enumerable.Range(0, 9).Select(i => -i) : [])
                    )
            )
        );
        await Assert.That(actual).IsEqualTo(expected);
    }
}
