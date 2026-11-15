namespace Tralys.Ecs.Tests;

public sealed class CaptureCompareTests
{
    [Test]
    public async Task References_compare_by_what_they_name_not_by_handle()
    {
        using var left = TestWorlds.Create();
        using var right = TestWorlds.Create();
        right.Destroy(right.Create());
        right.Playback();
        var (leftTarget, leftHolder) = Build(left);
        var (rightTarget, rightHolder) = Build(right);

        var sameShape = CaptureImage.Compare(
            left.Capture([leftTarget, leftHolder]),
            right.Capture([rightTarget, rightHolder])
        );
        right.Set(rightHolder, new Link { Target = rightHolder });
        var retargeted = CaptureImage.Compare(
            left.Capture([leftTarget, leftHolder]),
            right.Capture([rightTarget, rightHolder])
        );

        await Assert.That(leftTarget).IsNotEqualTo(rightTarget);
        await Assert.That(sameShape).IsEmpty();
        await Assert.That(retargeted.Count).IsEqualTo(1);
        await Assert.That(retargeted[0].Kind).IsEqualTo(DifferenceKind.ValueDiffers);
        await Assert.That(retargeted[0].Type).IsEqualTo(left.Registry.Get<Link>());
    }

    static (Entity Target, Entity Holder) Build(World world)
    {
        var target = world.Create();
        var holder = world.Create();
        world.Set(target, new Position { X = 1 });
        world.Set(holder, new Link { Target = target });
        return (target, holder);
    }
}
