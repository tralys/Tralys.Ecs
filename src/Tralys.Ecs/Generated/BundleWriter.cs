namespace Tralys.Ecs.Generated;

/// <summary>Writes a bundle's values straight into a fresh row, or records them into a command buffer.</summary>
public ref struct BundleWriter
{
    readonly World? world;
    readonly Archetype? archetype;
    readonly int row;
    readonly CommandBuffer? buffer;
    readonly Entity entity;

    internal BundleWriter(World world, Archetype archetype, int row)
    {
        this.world = world;
        this.archetype = archetype;
        this.row = row;
        buffer = null;
        entity = default;
    }

    internal BundleWriter(CommandBuffer buffer, Entity entity)
    {
        world = null;
        archetype = null;
        row = 0;
        this.buffer = buffer;
        this.entity = entity;
    }

    /// <summary>The generated <c>IBundle.Write</c> calls this once per non-tag field: it stores <paramref name="value"/> in the new row, or
    /// records it for playback when the entity is created through <see cref="Commands"/> or by an observer.</summary>
    public readonly void Write<T>(in T value)
        where T : struct, IComponent
    {
        if (buffer is not null)
        {
            if (buffer.parallel)
                buffer.RecordSet(entity, value);
            else
                buffer.world.StageBundled(entity, value);
            return;
        }
        if (world is null || archetype is null)
            return;
        var type = ComponentType<T>.Index;
        var column = archetype.ColumnSlot(type);
        if (column >= 0)
        {
            archetype.Values<T>(column)[row] = value;
            return;
        }
        if (world.layout.Class[type] == StorageClass.Sparse)
            world.AddSparse(archetype.Entities[row], type, value);
    }
}
