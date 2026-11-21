using Microsoft.CodeAnalysis;
using Tralys.Ecs.Generators.CodeGen;

namespace Tralys.Ecs.Generators.Metadata;

// With reach set, the walk is emitted outside the owner, so every field it reads must be visible from that assembly.
sealed class LayoutWalker(
    Compilation compilation,
    INamedTypeSymbol owner,
    IAssemblySymbol? reach = null
)
{
    const string Unsafe = "global::System.Runtime.CompilerServices.Unsafe";
    const int MaxDepth = 16;

    public readonly List<RefModel> EntityRefs = [];
    public readonly List<RefModel> BufferHandles = [];
    public readonly List<Problem> Problems = [];

    public void Walk() => Walk(owner, "v", 0, top: true);

    void Walk(ITypeSymbol type, string access, int depth, bool top)
    {
        if (depth > MaxDepth)
            return;
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (
                (field.AssociatedSymbol is not null || field.IsFixedSizeBuffer)
                && (ContainsEntity(field.Type, 0) || Contains(field.Type, Symbols.BufferHandle, 0))
            )
                Problems.Add(
                    new Problem(
                        Diagnostics.InaccessibleEntityField,
                        LocationInfo.From(owner.Locations.FirstOrDefault()),
                        Symbols.Name(owner),
                        Symbols.Name(type) + "." + (field.AssociatedSymbol?.Name ?? field.Name),
                        Symbols.Name(owner)
                    )
                );
        }
        foreach (var field in InstanceFields(type))
        {
            var fieldAccess = access + "." + field.Name;
            var visible = reach is null
                ? top || compilation.IsSymbolAccessibleWithin(field, owner)
                : compilation.IsSymbolAccessibleWithin(field, reach);
            if (!visible)
            {
                if (ContainsEntity(field.Type, 0))
                    Problems.Add(
                        new Problem(
                            Diagnostics.InaccessibleEntityField,
                            LocationInfo.From(owner.Locations.FirstOrDefault()),
                            Symbols.Name(owner),
                            Symbols.Name(type) + "." + field.Name,
                            Symbols.Name(owner)
                        )
                    );
                continue;
            }
            Visit(field.Type, fieldAccess, depth, Symbols.Name(type) + "." + field.Name);
        }
    }

    void Visit(ITypeSymbol type, string access, int depth, string field)
    {
        if (Symbols.Is(type, Symbols.Entity))
        {
            EntityRefs.Add(new RefModel(access, Symbols.Full(type), null));
            return;
        }
        if (Symbols.Is(type, Symbols.BufferHandle))
        {
            if (
                type is INamedTypeSymbol { TypeArguments.Length: 1 } handle
                && !Symbols.Is(handle.TypeArguments[0], Symbols.Entity)
                && ContainsEntity(handle.TypeArguments[0], 0)
            )
                Problems.Add(
                    new Problem(
                        Diagnostics.EntityInManagedCollection,
                        LocationInfo.From(owner.Locations.FirstOrDefault()),
                        field,
                        Symbols.Name(owner),
                        Symbols.Name(type)
                    )
                );
            BufferHandles.Add(new RefModel(access, Symbols.Full(type), null));
            return;
        }
        if (type is ITypeParameterSymbol parameter)
        {
            EntityRefs.Add(
                new RefModel(
                    access,
                    parameter.Name,
                    "typeof(" + parameter.Name + ") == typeof(global::Tralys.Ecs.Entity)"
                )
            );
            return;
        }
        if (
            !type.IsValueType
            || type.SpecialType != SpecialType.None
            || type.TypeKind == TypeKind.Enum
        )
            return;
        if (
            type is INamedTypeSymbol { IsGenericType: true } nullable
            && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
        )
        {
            var inner = nullable.TypeArguments[0];
            var value =
                $"{Unsafe}.As<{Symbols.Full(type)}, global::Tralys.Ecs.Generated.NullableLayout<{Symbols.Full(inner)}>>(ref {Unsafe}.AsRef(in {access})).Value";
            Visit(inner, value, depth + 1, field);
            return;
        }

        if (InlineArray(type) is { } inline)
        {
            for (var i = 0; i < inline.Length; i++)
            {
                var element =
                    $"{Unsafe}.Add(ref {Unsafe}.As<{Symbols.Full(type)}, {Symbols.Full(inline.Element)}>(ref {Unsafe}.AsRef(in {access})), {i})";
                Visit(inline.Element, element, depth + 1, field);
            }
            return;
        }
        Walk(type, access, depth + 1, top: false);
    }

    static IEnumerable<IFieldSymbol> InstanceFields(ITypeSymbol type) =>
        type.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f =>
                !f.IsStatic && !f.IsConst && f.AssociatedSymbol is null && !f.IsFixedSizeBuffer
            );

    static (int Length, ITypeSymbol Element)? InlineArray(ITypeSymbol type)
    {
        var attribute = type.GetAttributes()
            .FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString()
                == "System.Runtime.CompilerServices.InlineArrayAttribute"
            );
        if (
            attribute is null
            || attribute.ConstructorArguments.Length != 1
            || attribute.ConstructorArguments[0].Value is not int length
        )
            return null;
        var element = InstanceFields(type).FirstOrDefault();
        return element is null ? null : (length, element.Type);
    }

    public static bool ContainsEntity(ITypeSymbol type, int depth) =>
        Contains(type, Symbols.Entity, depth);

    static bool Contains(ITypeSymbol type, string symbol, int depth)
    {
        if (depth > MaxDepth)
            return false;
        if (Symbols.Is(type, symbol))
            return true;
        if (type is IArrayTypeSymbol array)
            return Contains(array.ElementType, symbol, depth + 1);
        if (type is INamedTypeSymbol named)
        {
            if (named.TypeArguments.Any(t => Contains(t, symbol, depth + 1)))
                return true;
            if (
                named.IsValueType
                && named.SpecialType == SpecialType.None
                && named.TypeKind != TypeKind.Enum
            )
                return named
                    .GetMembers()
                    .OfType<IFieldSymbol>()
                    .Any(f => !f.IsStatic && !f.IsConst && Contains(f.Type, symbol, depth + 1));
        }
        return false;
    }
}
