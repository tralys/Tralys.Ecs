using Microsoft.CodeAnalysis;

namespace Tralys.Ecs.Generators;

// The values must match Tralys.Ecs.Generated.Effects, whose raw value the attribute carries.
static class Effects
{
    public const int None = 0;
    public const int Structural = 1 << 2;
    public const int MovesEntities = 1 << 3;
    public const int Mutates = 1 << 4;
    public const int Settled = 1 << 5;
    public const int OwnerThread = 1 << 7;
    public const int MovesRowBlocks = 1 << 8;
    public const int Moves = MovesEntities | MovesRowBlocks;

    public const string Attribute = "Tralys.Ecs.Generated.EffectAttribute";
    public const string Borrow = "Tralys.Ecs.Generated.BorrowAttribute";

    static readonly (int Flag, string Name)[] Names =
    [
        (Structural, "Structural"),
        (MovesEntities, "MovesEntities"),
        (Mutates, "Mutates"),
        (Settled, "Settled"),
        (OwnerThread, "OwnerThread"),
        (MovesRowBlocks, "MovesRowBlocks"),
    ];

    public static IEnumerable<int> Flags(int flags) =>
        Names.Where(n => (flags & n.Flag) != 0).Select(n => n.Flag);

    public static string Describe(int flags) =>
        flags == None
            ? "None"
            : string.Join(" | ", Names.Where(n => (flags & n.Flag) != 0).Select(n => n.Name));

    public static string Declare(int flags) =>
        "[global::Tralys.Ecs.Generated.Effect("
        + string.Join(
            " | ",
            Describe(flags)
                .Split([" | "], StringSplitOptions.None)
                .Select(name => "global::Tralys.Ecs.Generated.Effects." + name)
        )
        + ")]";

    public static int? Declared(ISymbol symbol)
    {
        var definition = symbol switch
        {
            IMethodSymbol method => (method.ReducedFrom ?? method).OriginalDefinition,
            _ => symbol.OriginalDefinition,
        };
        if (Symbols.Attribute(definition, Attribute) is not { } attribute)
            return null;
        return
            attribute.ConstructorArguments.Length == 1
            && attribute.ConstructorArguments[0].Value is int value
            ? value
            : None;
    }

    public static bool IsBorrow(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && Symbols.Has(named.OriginalDefinition, Borrow);

    public static bool IsRuntime(Compilation compilation) =>
        compilation.GetTypeByMetadataName(Attribute) is { } attribute
        && SymbolEqualityComparer.Default.Equals(
            attribute.ContainingAssembly,
            compilation.Assembly
        );
}
