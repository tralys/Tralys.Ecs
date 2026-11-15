using System.Runtime.InteropServices;

namespace Tralys.Ecs.Tests;

public sealed class FieldPolicyTests
{
    sealed class Shift(double by) : IFieldRebaser
    {
        public void Rebase(FieldDescriptor field, Span<byte> value) =>
            MemoryMarshal.Write(value, MemoryMarshal.Read<double>(value) + by);
    }

    [Test]
    public async Task A_restore_defaults_skipped_fields_and_rebases_rebased_ones()
    {
        using var source = TestWorlds.Create();
        var entity = source.Create();
        source.Set(
            entity,
            new Cooldown
            {
                ReadyAt = 10,
                LocalTicket = 99,
                Charges = 3,
            }
        );
        var image = source.Capture([entity]);

        using var target = TestWorlds.Create();
        var restored = target
            .Restore(image, new RestoreOptions(Rebaser: new Shift(-4)))
            .Map(entity);
        var cooldown = target.Get<Cooldown>(restored);

        await Assert.That(cooldown.LocalTicket).IsEqualTo(0);
        await Assert.That(cooldown.Charges).IsEqualTo(3);
        await Assert.That(cooldown.ReadyAt).IsEqualTo(6);
    }

    sealed class Refuse : IFieldRebaser
    {
        public void Rebase(FieldDescriptor field, Span<byte> value) =>
            throw new InvalidOperationException("refused");
    }

    [Test]
    public async Task A_rebaser_that_throws_leaves_the_target_world_untouched()
    {
        using var source = TestWorlds.Create();
        var first = source.Create();
        source.Set(first, new Cooldown { ReadyAt = 1 });
        var second = source.Create();
        source.Set(second, new Cooldown { ReadyAt = 2 });
        var image = source.Capture([first, second]);

        using var target = TestWorlds.Create();
        var threw = false;
        try
        {
            target.Restore(image, new RestoreOptions(Rebaser: new Refuse()));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        var next = target.Create();

        await Assert.That(threw).IsTrue();
        await Assert.That(next.Index).IsEqualTo(1u);
    }
}
