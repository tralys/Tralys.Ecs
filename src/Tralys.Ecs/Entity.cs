using System.Runtime.CompilerServices;

namespace Tralys.Ecs;

/// <summary>An 8-byte entity handle: a slot index and the slot's generation. It names no world.</summary>
public readonly struct Entity : IEquatable<Entity>
{
    internal const uint PlaceholderBit = 0x8000_0000;

    readonly ulong value;

    /// <summary>The handle to slot <paramref name="index"/> at <paramref name="generation"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> has its top bit set, which marks a command-buffer
    /// placeholder.</exception>
    public Entity(uint index, uint generation)
    {
        if ((index & PlaceholderBit) != 0)
            ThrowPlaceholder(nameof(index));
        value = ((ulong)generation << 32) | index;
    }

    Entity(ulong value) => this.value = value;

    /// <summary>The slot index; once the entity is finalised the slot is reused under the next generation.</summary>
    public uint Index => (uint)value;

    /// <summary>The slot's generation when the handle was issued; once the slot is freed, the handle names nothing.</summary>
    public uint Generation => (uint)(value >> 32);

    /// <summary>The handle as one value: <see cref="Generation"/> in the high 32 bits, <see cref="Index"/> in the low. <see cref="FromBits"/>
    /// restores it.</summary>
    public ulong Bits => value;

    /// <summary>The handle whose <see cref="Bits"/> are <paramref name="bits"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index half has its top bit set, which marks a command-buffer placeholder;
    /// placeholders never leave the buffer that issued them.</exception>
    public static Entity FromBits(ulong bits)
    {
        if ((bits & PlaceholderBit) != 0)
            ThrowPlaceholder(nameof(bits));
        return new(bits);
    }

    /// <summary>The handle is <c>default(Entity)</c>, which no world ever issues; whether an issued handle is still usable is
    /// <see cref="World.IsLive(Entity)"/>.</summary>
    public bool IsDefault => value == 0;

    internal bool IsPlaceholder => (Index & PlaceholderBit) != 0;

    internal static Entity Placeholder(uint rental, int local) =>
        new(((ulong)rental << 32) | PlaceholderBit | (uint)local);

    internal static Entity Unchecked(ulong bits) => new(bits);

    internal int PlaceholderLocal => (int)(Index & ~PlaceholderBit);
    internal uint PlaceholderRental => Generation;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowPlaceholder(string parameter) =>
        throw new ArgumentOutOfRangeException(
            parameter,
            "The top bit of an entity index marks a command-buffer placeholder; a real handle never has it set."
        );

    /// <summary>The same index and generation.</summary>
    public bool Equals(Entity other) => value == other.value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Entity other && value == other.value;

    /// <inheritdoc/>
    public override int GetHashCode() => value.GetHashCode();

    /// <summary>The same index and generation.</summary>
    public static bool operator ==(Entity left, Entity right) => left.value == right.value;

    /// <summary>A different index or generation.</summary>
    public static bool operator !=(Entity left, Entity right) => left.value != right.value;

    /// <summary>Such as <c>Entity(3v1)</c> for index 3 at generation 1, or <c>Entity(null)</c>; a placeholder shows its buffer-local
    /// index.</summary>
    public override string ToString() =>
        IsDefault ? "Entity(null)"
        : IsPlaceholder
            ? $"Entity(placeholder {PlaceholderLocal} of buffer rental {PlaceholderRental})"
        : $"Entity({Index}v{Generation})";
}
