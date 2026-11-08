using System.Runtime.CompilerServices;

namespace Tralys.Ecs.Tests;

public sealed class EntityOffsetTests
{
    [Test]
    public async Task Every_inline_array_element_has_its_own_entity_offset()
    {
        var squad = new Squad();
        for (var i = 0; i < 4; i++)
            squad.Members[i] = new Entity((uint)i + 1, 7);

        var read = new List<Entity>();
        foreach (var offset in TestWorlds.Registry.Get<Squad>().EntityRefOffsets)
            read.Add(
                Unsafe.As<byte, Entity>(
                    ref Unsafe.Add(ref Unsafe.As<Squad, byte>(ref squad), offset)
                )
            );

        await Assert
            .That(read)
            .IsEquivalentTo([
                squad.Members[0],
                squad.Members[1],
                squad.Members[2],
                squad.Members[3],
            ]);
    }

    [Test]
    public async Task A_nested_struct_field_has_its_entity_offset()
    {
        var escort = new Escort
        {
            Ward = new Ward { Rank = 3, Charge = new Entity(9, 2) },
        };

        var read = new List<Entity>();
        foreach (var offset in TestWorlds.Registry.Get<Escort>().EntityRefOffsets)
            read.Add(
                Unsafe.As<byte, Entity>(
                    ref Unsafe.Add(ref Unsafe.As<Escort, byte>(ref escort), offset)
                )
            );

        await Assert.That(read).IsEquivalentTo([escort.Ward.Charge]);
    }
}
