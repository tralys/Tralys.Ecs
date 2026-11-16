using System.Runtime.InteropServices;

namespace Tralys.Ecs;

/// <summary>Whole-world comparison for determinism tests.</summary>
public static class WorldDiff
{
    /// <summary>Captures every live entity of both worlds as a root, in handle order, with default <see cref="CaptureOptions"/>, and
    /// compares the captures by <see cref="CaptureImage.Compare"/>. Worlds built by the same operations pair entity for entity.</summary>
    /// <exception cref="ArgumentException">The worlds have different registries.</exception>
    public static IReadOnlyList<WorldDifference> Compare(World left, World right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return CaptureImage.Compare(CaptureAll(left), CaptureAll(right));
    }

    static CaptureImage CaptureAll(World world)
    {
        var roots = new List<Entity>();
        foreach (var archetype in world.Introspection.Archetypes)
        {
            foreach (var entity in archetype.Entities)
            {
                if (world.IsLive(entity))
                    roots.Add(entity);
            }
        }
        roots.Sort((x, y) => x.Index.CompareTo(y.Index));
        return world.Capture(CollectionsMarshal.AsSpan(roots));
    }
}
