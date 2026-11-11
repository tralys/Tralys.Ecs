namespace Tralys.Ecs.Tests;

public sealed partial class SystemDescriptorTests
{
    [System]
    sealed partial class Drifter : SystemBase
    {
        [Query(Parallel = true)]
        static void Drift(ref Position position, in Velocity velocity) => position.X += velocity.X;

        protected override void OnRun(in RunContext context) => RunDrift(context.World);
    }

    sealed partial class DriftHost
    {
        [Query(Parallel = true)]
        internal static void Drift(ref Position position, in Velocity velocity) =>
            position.X += velocity.X;
    }

    [System]
    sealed partial class Delegator : SystemBase
    {
        protected override void OnRun(in RunContext context) => DriftHost.RunDrift(context.World);
    }

    [System]
    sealed partial class Walker : SystemBase
    {
        [Query]
        static void Walk(ref Position position, in Velocity velocity) => position.X += velocity.X;

        protected override void OnRun(in RunContext context) => RunWalk(context.World);
    }

    [Test]
    public async Task A_system_running_a_parallel_query_of_its_own_or_a_host_s_is_parallel()
    {
        await Assert
            .That(
                (
                    Drifter.Descriptor.Parallel,
                    Delegator.Descriptor.Parallel,
                    Walker.Descriptor.Parallel
                )
            )
            .IsEqualTo((true, true, false));
    }
}
