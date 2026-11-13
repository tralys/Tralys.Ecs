using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators.Indexes;

static class IndexModelBuilder
{
    public static string Build(
        INamedTypeSymbol type,
        DeclKind kind,
        bool generic,
        List<AttributeData> attributes,
        List<IndexModel> indexes,
        List<Problem> problems
    )
    {
        string? access = Symbols.Visibility(type, "public");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < attributes.Count; ordinal++)
        {
            var attribute = attributes[ordinal];
            var index = Index(
                type,
                attribute,
                ordinal,
                out var name,
                out var keys,
                out var problem
            );
            if (kind != DeclKind.Component)
                problem = "only components with a value can be indexed";
            else if (generic || Symbols.InGenericType(type))
                problem = "indexes on generic components are not supported";
            else if (index is not null)
            {
                var keyAccess = access;
                foreach (var key in keys)
                    keyAccess = Symbols.Visibility(key, keyAccess);
                if (keyAccess is null)
                    problem =
                        "the component, its containers and the key's types must be public or internal to get index accessors";
                else if (!names.Add(name))
                    problem = $"another index of the type is named '{name}'";
                else
                    access = keyAccess;
            }
            if (problem is not null)
            {
                problems.Add(
                    new Problem(
                        Diagnostics.InvalidIndex,
                        LocationInfo.From(
                            attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                        ) ?? LocationInfo.From(type.Locations.FirstOrDefault()),
                        name,
                        Symbols.Name(type),
                        problem
                    )
                );
                continue;
            }
            indexes.Add(index!);
        }
        return access ?? "";
    }

    public static IEnumerable<string> AccessorNames(INamedTypeSymbol type)
    {
        var ordinal = 0;
        foreach (var attribute in type.GetAttributes())
        {
            if (Symbols.IndexKindOf(attribute) is null)
                continue;
            Index(type, attribute, ordinal++, out var name, out _, out _);
            yield return Symbols.DeclarationName(type) + name;
        }
    }

    public static List<string> Fields(AttributeData attribute, out int kind)
    {
        kind = Symbols.IndexKindOf(attribute) ?? 0;
        var fields = new List<string>();
        if (
            attribute.ConstructorArguments.Length == 1
            && attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array
        )
        {
            foreach (var value in attribute.ConstructorArguments[0].Values)
            {
                if (value.Value is string field)
                    fields.Add(field);
            }
        }
        return fields;
    }

    static IndexModel? Index(
        INamedTypeSymbol type,
        AttributeData attribute,
        int ordinal,
        out string name,
        out List<ITypeSymbol> keys,
        out string? problem
    )
    {
        keys = [];
        var fields = Fields(attribute, out var kind);
        var keyFrom = Symbols.Named<string?>(attribute, "KeyFrom", null);
        var perParent = Symbols.Named(attribute, "PerParent", false);
        name =
            Symbols.Named<string?>(attribute, "Name", null)
            ?? "By" + (keyFrom ?? string.Concat(fields));
        problem = null;
        if (keyFrom is not null && fields.Count > 0)
            problem = "give the key's fields or KeyFrom, not both";
        else if (keyFrom is null && fields.Count == 0)
            problem = "name the key's fields or a KeyFrom method";
        else if (kind == 2 && fields.Count > 1)
            problem =
                "an ordered index takes one key field: Due bounds keys only from above, so a leading field cannot scope it";
        else if (!SyntaxFacts.IsValidIdentifier(name))
            problem = $"'{name}' is not a valid accessor name";
        if (problem is not null)
            return null;

        string keyType;
        string expression;
        string? tryCall = null;
        if (keyFrom is not null)
        {
            var methods = type.GetMembers(keyFrom).OfType<IMethodSymbol>().ToList();
            if (methods.Count != 1 || !IsKeyFrom(methods[0], type))
            {
                problem =
                    $"KeyFrom '{keyFrom}' must name one static, non-generic method of the type that takes the component by value or by in and either returns the key, or returns bool and gives the key as an out parameter";
                return null;
            }
            var method = methods[0];
            var key = KeyOf(method);
            keys.Add(key);
            keyType = Symbols.Full(key);
            var pass = method.Parameters[0].RefKind == RefKind.None ? "" : "in ";
            if (method.Parameters.Length == 2)
            {
                tryCall = $"{Symbols.Full(type)}.{keyFrom}({pass}v, out var k)";
                expression = "k";
            }
            else
                expression = $"{Symbols.Full(type)}.{keyFrom}({pass}v)";
        }
        else
        {
            var types = new List<string>();
            foreach (var field in fields)
            {
                if (
                    type.GetMembers(field)
                        .OfType<IFieldSymbol>()
                        .FirstOrDefault(f => !f.IsStatic && !f.IsConst)
                    is not { } symbol
                )
                {
                    problem = $"'{field}' is not an instance field of the type";
                    return null;
                }
                keys.Add(symbol.Type);
                types.Add(Symbols.Full(symbol.Type));
            }
            keyType = types.Count == 1 ? types[0] : "(" + string.Join(", ", types) + ")";
            expression =
                fields.Count == 1
                    ? "v." + fields[0]
                    : "(" + string.Join(", ", fields.Select(f => "v." + f)) + ")";
        }
        if (perParent)
        {
            keyType = $"(global::Tralys.Ecs.Entity, {keyType})";
            expression = $"(parent, {expression})";
        }
        return new IndexModel(
            ordinal,
            kind,
            name,
            new([.. fields]),
            keyFrom,
            Symbols.Named(attribute, "OnConflict", 0),
            perParent,
            keyType,
            expression,
            tryCall
        );
    }

    public static ITypeSymbol KeyOf(IMethodSymbol keyFrom) =>
        keyFrom.Parameters.Length == 2 ? keyFrom.Parameters[1].Type : keyFrom.ReturnType;

    static bool IsKeyFrom(IMethodSymbol method, INamedTypeSymbol type)
    {
        if (
            method
                is not {
                    IsStatic: true,
                    IsGenericMethod: false,
                    ReturnsVoid: false,
                    ReturnsByRef: false,
                    ReturnsByRefReadonly: false,
                    Parameters.Length: 1 or 2,
                }
            || !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type)
            || method.Parameters[0].RefKind
                is not (RefKind.None or RefKind.In or RefKind.RefReadOnlyParameter)
        )
            return false;
        return method.Parameters.Length == 1
            || (
                method.ReturnType.SpecialType == SpecialType.System_Boolean
                && method.Parameters[1].RefKind == RefKind.Out
            );
    }
}
