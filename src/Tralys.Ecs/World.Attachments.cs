using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>App-owned per-world state with lifecycle hooks. Attachments are slots, not a service container: the world constructs nothing.</summary>
public interface IWorldAttachment
{
    /// <summary>Runs at the end of <see cref="World.Clear"/>, once the world is empty.</summary>
    [Effect(
        Effects.Structural
            | Effects.MovesEntities
            | Effects.Mutates
            | Effects.OwnerThread
            | Effects.MovesRowBlocks
    )]
    void OnWorldCleared(World world);

    /// <summary>Runs after <see cref="World.Reset"/> or <see cref="World.Restore"/>, once the world's data is in place.</summary>
    [Effect(
        Effects.Structural
            | Effects.MovesEntities
            | Effects.Mutates
            | Effects.OwnerThread
            | Effects.MovesRowBlocks
    )]
    void OnWorldRestored(World world, in RestoreInfo info);
}

internal static class AttachmentSlot<T>
    where T : class, IWorldAttachment
{
    public static readonly int Index = AttachmentIds.Next();
}

internal static class AttachmentIds
{
    static int next;

    public static int Next() => Interlocked.Increment(ref next) - 1;
}

public sealed partial class World
{
    IWorldAttachment?[] attachments = [];
    readonly List<IWorldAttachment> attachmentOrder = [];

    /// <summary>The attached <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">No <typeparamref name="T"/> is attached.</exception>
    [Effect(Effects.None)]
    public T GetAttachment<T>()
        where T : class, IWorldAttachment =>
        TryGetAttachment<T>(out var attachment)
            ? attachment
            : throw new InvalidOperationException(
                $"No {typeof(T).Name} is attached to this world."
            );

    /// <summary>The attached <typeparamref name="T"/>; false when none is attached.</summary>
    [Effect(Effects.None)]
    public bool TryGetAttachment<T>([NotNullWhen(true)] out T? attachment)
        where T : class, IWorldAttachment
    {
        var index = AttachmentSlot<T>.Index;
        attachment = index < attachments.Length ? Unsafe.As<T>(attachments[index]) : null;
        return attachment is not null;
    }

    /// <summary>Attaches app-owned state; hooks run in attach order.</summary>
    [Effect(Effects.Mutates)]
    public void SetAttachment<T>(T attachment)
        where T : class, IWorldAttachment
    {
        CheckWritable();
        var index = AttachmentSlot<T>.Index;
        if (index >= attachments.Length)
            Array.Resize(ref attachments, Math.Max(index + 1, attachments.Length * 2));
        if (attachments[index] is { } previous)
            attachmentOrder.Remove(previous);
        attachments[index] = attachment;
        attachmentOrder.Add(attachment);
    }
}
