using Microsoft.CodeAnalysis;

namespace Tralys.Ecs.Generators;

// The values must match Tralys.Ecs.Generated.Effects, whose raw value the attribute carries.
static class Effects
{
    public const int Structural = 1 << 2;
    public const int MovesRows = 1 << 3;
    public const int Mutates = 1 << 4;
    public const int Settled = 1 << 5;
    public const int Borrows = 1 << 6;
    public const int OwnerThread = 1 << 7;

    const string Attribute = "Tralys.Ecs.Generated.EffectAttribute";

    public static Effect? Of(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (Symbols.Attribute(definition, Attribute) is not { } attribute)
            return null;
        var flags =
            attribute.ConstructorArguments.Length == 1
            && attribute.ConstructorArguments[0].Value is int value
                ? value
                : 0;
        return new Effect(flags);
    }
}

readonly record struct Effect(int Flags)
{
    public bool Has(int flag) => (Flags & flag) != 0;
}
