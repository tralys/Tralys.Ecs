using BenchmarkDotNet.Running;
using Tralys.Ecs.Spikes;

BenchmarkSwitcher.FromAssembly(typeof(SpawnOrdinalSpike).Assembly).Run(args);
