namespace Tralys.Ecs;

internal readonly record struct LogKinds(uint Bits)
{
    public static readonly LogKinds All = Every();

    public static LogKinds Of(params ReadOnlySpan<LogKind> kinds)
    {
        var bits = 0u;
        foreach (var kind in kinds)
            bits |= 1u << (int)kind;
        return new LogKinds(bits);
    }

    public bool Has(LogKind kind) => (Bits & (1u << (int)kind)) != 0;

    public bool Overlaps(LogKinds other) => (Bits & other.Bits) != 0;

    public static LogKinds operator |(LogKinds a, LogKinds b) => new(a.Bits | b.Bits);

    static LogKinds Every()
    {
        var kinds = Enum.GetValues<LogKind>();
        foreach (var kind in kinds)
        {
            if ((int)kind >= 32)
                throw new InvalidOperationException(
                    $"LogKinds holds 32 kinds; {kind} is number {(int)kind}."
                );
        }
        return Of(kinds);
    }
}
