namespace Tralys.Ecs.Tests;

public sealed class ChangeModelTests
{
    const int Sequences = 400;

    enum Op
    {
        Set,
        Remove,
        Enable,
        Disable,
    }

    record struct Step(int Entity, Op Op, int Value);

    sealed class Hears : IAddObserver<Seal>, IRemoveObserver<Seal>, IReplaceObserver<Seal>
    {
        public readonly List<string> Seen = [];
        public Entity[] Entities = [];

        string Name(Entity entity) => "e" + Array.IndexOf(Entities, entity);

        public void OnAdd(in ObserverContext context, Entity entity, in Seal value) =>
            Seen.Add($"{Name(entity)}+{value.Mark}");

        public void OnRemove(in ObserverContext context, Entity entity, in Seal oldValue) =>
            Seen.Add($"{Name(entity)}-{oldValue.Mark}");

        public void OnReplace(
            in ObserverContext context,
            Entity entity,
            in Seal oldValue,
            in Seal newValue
        ) => Seen.Add($"{Name(entity)}{oldValue.Mark}>{newValue.Mark}");
    }

    struct Model
    {
        public bool Stored;
        public bool Enabled;
        public int Value;
    }

    static Step[] Steps(Random random, Model[] start)
    {
        var model = (Model[])start.Clone();
        var steps = new List<Step>();
        var length = random.Next(1, 7);
        while (steps.Count < length)
        {
            var e = random.Next(model.Length);
            var op = (Op)random.Next(4);
            if (op == Op.Enable && !model[e].Stored)
                continue;
            var value = random.Next(1, 4);
            steps.Add(new Step(e, op, value));
            Apply(ref model[e], op, value);
        }
        return [.. steps];
    }

    static void Apply(ref Model model, Op op, int value)
    {
        switch (op)
        {
            case Op.Set:
                model = new Model
                {
                    Stored = true,
                    Enabled = true,
                    Value = value,
                };
                break;
            case Op.Remove:
                model.Stored = false;
                break;
            case Op.Enable:
                model.Enabled = true;
                break;
            case Op.Disable:
                if (model.Stored)
                    model.Enabled = false;
                break;
        }
    }

    static (Model[] Start, World World, Entity[] Entities) Arrange(Random random)
    {
        var world = TestWorlds.Logged();
        var start = new Model[2];
        var entities = new Entity[2];
        for (var e = 0; e < 2; e++)
        {
            entities[e] = world.Create();
            if (random.Next(3) == 0)
                continue;
            start[e] = new Model
            {
                Stored = true,
                Enabled = random.Next(2) == 0,
                Value = random.Next(1, 4),
            };
            world.Set(entities[e], new Seal { Mark = start[e].Value });
            world.SetEnabled<Seal>(entities[e], start[e].Enabled);
        }
        world.Playback();
        return (start, world, entities);
    }

    static (Hears Hears, LogTail Tail) Watch(World world, Entity[] entities)
    {
        var hears = new Hears { Entities = entities };
        world.Observers.OnAdd(hears);
        world.Observers.OnRemove(hears);
        world.Observers.OnReplace(hears);
        return (hears, new LogTail(world));
    }

    static List<string> Logged(LogTail tail, Entity[] entities)
    {
        var logged = new List<string>();
        foreach (var entry in tail.Read())
        {
            if (entry.Is<Seal>())
                logged.Add($"e{Array.IndexOf(entities, entry.Entity)}:{entry.Kind}");
        }
        return logged;
    }

    static string Final(World world, Entity[] entities) =>
        string.Join(
            ",",
            entities.Select(e =>
                world.TryGetStored<Seal>(e, out var seal)
                    ? $"{world.Has<Seal>(e)}:{seal.Mark}"
                    : "none"
            )
        );

    static string Final(Model[] models) =>
        string.Join(",", models.Select(m => m.Stored ? $"{m.Enabled}:{m.Value}" : "none"));

    // What each write reports on its own: the log follows storage, observers follow presence.
    static void Expect(
        ref Model model,
        int e,
        Op op,
        int value,
        List<string> heard,
        List<string> logged
    )
    {
        var before = model;
        Apply(ref model, op, value);
        var stored = (before.Stored, model.Stored);
        var present = (before.Stored && before.Enabled, model.Stored && model.Enabled);
        if (stored == (true, false))
            logged.Add($"e{e}:Removed");
        if (stored == (false, true))
            logged.Add($"e{e}:Added");
        if (stored == (true, true) && before.Enabled != model.Enabled)
            logged.Add($"e{e}:{(model.Enabled ? "Enabled" : "Disabled")}");
        if (present == (true, false))
            heard.Add($"e{e}-{before.Value}");
        if (present == (false, true))
            heard.Add($"e{e}+{model.Value}");
        if (present == (true, true) && before.Value != model.Value)
        {
            logged.Add($"e{e}:Replaced");
            heard.Add($"e{e}{before.Value}>{model.Value}");
        }
    }

    [Test]
    public async Task Immediate_writes_report_each_write_as_the_model_does()
    {
        var random = new Random(41);
        var mismatches = new List<string>();
        for (var run = 0; run < Sequences; run++)
        {
            var (start, world, entities) = Arrange(random);
            using var _ = world;
            var steps = Steps(random, start);
            var (hears, tail) = Watch(world, entities);
            var model = (Model[])start.Clone();
            var heard = new List<string>();
            var logged = new List<string>();

            foreach (var step in steps)
            {
                var entity = entities[step.Entity];
                switch (step.Op)
                {
                    case Op.Set:
                        world.Set(entity, new Seal { Mark = step.Value });
                        break;
                    case Op.Remove:
                        world.Remove<Seal>(entity);
                        break;
                    case Op.Enable:
                        world.SetEnabled<Seal>(entity, true);
                        break;
                    case Op.Disable:
                        world.SetEnabled<Seal>(entity, false);
                        break;
                }
                Expect(ref model[step.Entity], step.Entity, step.Op, step.Value, heard, logged);
            }
            world.Playback();

            Compare(
                run,
                start,
                steps,
                heard,
                hears.Seen,
                logged,
                Logged(tail, entities),
                Final(model),
                Final(world, entities),
                mismatches
            );
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task A_recorded_batch_reports_each_component_s_net_change_as_the_model_does()
    {
        var random = new Random(43);
        var mismatches = new List<string>();
        for (var run = 0; run < Sequences; run++)
        {
            var (start, world, entities) = Arrange(random);
            using var _ = world;
            var steps = Steps(random, start);
            var (hears, tail) = Watch(world, entities);
            var model = (Model[])start.Clone();
            var removed = new bool[entities.Length];
            var order = new List<int>();

            var buffer = world.RentBuffer(world.runSequence, 0);
            foreach (var step in steps)
            {
                var entity = entities[step.Entity];
                switch (step.Op)
                {
                    case Op.Set:
                        buffer.Commands.Set(entity, new Seal { Mark = step.Value });
                        break;
                    case Op.Remove:
                        buffer.Commands.Remove<Seal>(entity);
                        break;
                    case Op.Enable:
                        buffer.Commands.SetEnabled<Seal>(entity, true);
                        break;
                    case Op.Disable:
                        buffer.Commands.SetEnabled<Seal>(entity, false);
                        break;
                }
                if (!order.Contains(step.Entity))
                    order.Add(step.Entity);
                removed[step.Entity] |= step.Op == Op.Remove && model[step.Entity].Stored;
                Apply(ref model[step.Entity], step.Op, step.Value);
            }
            world.Submit(buffer);
            world.Playback();

            var heard = new List<string>();
            var logged = new List<string>();
            foreach (var e in order)
            {
                var before = start[e];
                var after = model[e];
                var readded = before.Stored && after.Stored && removed[e];
                var presentBefore = before.Stored && before.Enabled;
                var presentAfter = after.Stored && after.Enabled;
                if (before.Stored && (!after.Stored || readded))
                    logged.Add($"e{e}:Removed");
                if (after.Stored && (!before.Stored || readded))
                {
                    logged.Add($"e{e}:Added");
                    if (!after.Enabled)
                        logged.Add($"e{e}:Disabled");
                }
                else if (before.Stored && after.Stored && before.Enabled != after.Enabled)
                    logged.Add($"e{e}:{(after.Enabled ? "Enabled" : "Disabled")}");
                var replaced =
                    presentBefore && presentAfter && !readded && before.Value != after.Value;
                if (replaced)
                    logged.Add($"e{e}:Replaced");
                if (presentBefore && (!presentAfter || readded))
                    heard.Add($"e{e}-{before.Value}");
                if (presentAfter && (!presentBefore || readded))
                    heard.Add($"e{e}+{after.Value}");
                if (replaced)
                    heard.Add($"e{e}{before.Value}>{after.Value}");
            }

            Compare(
                run,
                start,
                steps,
                heard,
                hears.Seen,
                logged,
                Logged(tail, entities),
                Final(model),
                Final(world, entities),
                mismatches
            );
        }

        await Assert.That(mismatches).IsEmpty();
    }

    // Inside an observer a value write lands in place until the component has a presence change waiting; from then on every write is
    // recorded and playback reports the net change from what storage holds.
    [Test]
    public async Task Writes_inside_an_observer_report_as_the_model_does()
    {
        var random = new Random(47);
        var mismatches = new List<string>();
        for (var run = 0; run < Sequences; run++)
        {
            var (start, world, entities) = Arrange(random);
            using var _ = world;
            var steps = Steps(random, start);
            var (hears, tail) = Watch(world, entities);
            var storage = (Model[])start.Clone();
            var logical = (Model[])start.Clone();
            var pending = new bool[entities.Length];
            var removed = new bool[entities.Length];
            var order = new List<int>();
            var heard = new List<string>();
            var logged = new List<string>();

            using (world.BeginDelivery())
            {
                foreach (var step in steps)
                {
                    var e = step.Entity;
                    var entity = entities[e];
                    ref var now = ref logical[e];
                    switch (step.Op)
                    {
                        case Op.Set:
                            world.Set(entity, new Seal { Mark = step.Value });
                            if (!pending[e] && now.Stored && now.Enabled)
                            {
                                if (now.Value != step.Value)
                                {
                                    heard.Add($"e{e}{now.Value}>{step.Value}");
                                    logged.Add($"e{e}:Replaced");
                                }
                                storage[e].Value = step.Value;
                                now.Value = step.Value;
                                continue;
                            }
                            break;
                        case Op.Remove:
                            world.Remove<Seal>(entity);
                            if (!now.Stored)
                                continue;
                            removed[e] = true;
                            break;
                        case Op.Enable:
                            world.SetEnabled<Seal>(entity, true);
                            if (now.Enabled && !pending[e])
                                continue;
                            break;
                        case Op.Disable:
                            world.SetEnabled<Seal>(entity, false);
                            if (!(now.Stored && now.Enabled) && !pending[e])
                                continue;
                            break;
                    }
                    if (!pending[e])
                    {
                        pending[e] = true;
                        order.Add(e);
                    }
                    Apply(ref now, step.Op, step.Value);
                }
            }
            world.Playback();

            foreach (var e in order)
            {
                var before = storage[e];
                var after = logical[e];
                var readded = before.Stored && after.Stored && removed[e];
                var presentBefore = before.Stored && before.Enabled;
                var presentAfter = after.Stored && after.Enabled;
                if (before.Stored && (!after.Stored || readded))
                    logged.Add($"e{e}:Removed");
                if (after.Stored && (!before.Stored || readded))
                {
                    logged.Add($"e{e}:Added");
                    if (!after.Enabled)
                        logged.Add($"e{e}:Disabled");
                }
                else if (before.Stored && after.Stored && before.Enabled != after.Enabled)
                    logged.Add($"e{e}:{(after.Enabled ? "Enabled" : "Disabled")}");
                var replaced =
                    presentBefore && presentAfter && !readded && before.Value != after.Value;
                if (replaced)
                    logged.Add($"e{e}:Replaced");
                if (presentBefore && (!presentAfter || readded))
                    heard.Add($"e{e}-{before.Value}");
                if (presentAfter && (!presentBefore || readded))
                    heard.Add($"e{e}+{after.Value}");
                if (replaced)
                    heard.Add($"e{e}{before.Value}>{after.Value}");
            }

            Compare(
                run,
                start,
                steps,
                heard,
                hears.Seen,
                logged,
                Logged(tail, entities),
                Final(logical),
                Final(world, entities),
                mismatches
            );
        }

        await Assert.That(mismatches).IsEmpty();
    }

    static void Compare(
        int run,
        Model[] start,
        Step[] steps,
        List<string> expectedHeard,
        List<string> heard,
        List<string> expectedLogged,
        List<string> logged,
        string expectedFinal,
        string final,
        List<string> mismatches
    )
    {
        if (
            expectedHeard.SequenceEqual(heard)
            && expectedLogged.SequenceEqual(logged)
            && expectedFinal == final
        )
            return;
        mismatches.Add(
            $"run {run}: start {Final(start)}; steps {string.Join(" ", steps.Select(s => $"e{s.Entity}.{s.Op}({s.Value})"))}; "
                + $"heard [{string.Join(" ", heard)}] expected [{string.Join(" ", expectedHeard)}]; "
                + $"logged [{string.Join(" ", logged)}] expected [{string.Join(" ", expectedLogged)}]; "
                + $"final {final} expected {expectedFinal}"
        );
    }
}
