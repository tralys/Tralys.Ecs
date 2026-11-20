using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators;

static class Symbols
{
    public const string Component = "Tralys.Ecs.ComponentAttribute";
    public const string Tag = "Tralys.Ecs.TagAttribute";
    public const string Relation = "Tralys.Ecs.RelationAttribute";
    public const string Rows = "Tralys.Ecs.RowsAttribute";
    public const string Bundle = "Tralys.Ecs.BundleAttribute";
    public const string Instance = "Tralys.Ecs.ComponentInstanceAttribute`1";
    public const string Requires = "Tralys.Ecs.RequiresAttribute`1";
    public const string Entity = "Tralys.Ecs.Entity";
    public const string BufferHandle = "Tralys.Ecs.BufferHandle`1";
    public const string BufferArena = "Tralys.Ecs.BufferArena`1";
    public const string Commands = "Tralys.Ecs.Commands";
    public const string ChunkView = "Tralys.Ecs.ChunkView";
    public const string CommandRows = "Tralys.Ecs.CommandRows`2";
    public const string ComponentEdit = "Tralys.Ecs.ComponentEdit`1";
    public const string RowEdit = "Tralys.Ecs.RowEdit`2";
    public const string ScratchBuffer = "Tralys.Ecs.ScratchBuffer`1";
    public const string IComponent = "Tralys.Ecs.IComponent";
    public const string Query = "Tralys.Ecs.QueryAttribute";
    public const string Collect = "Tralys.Ecs.CollectAttribute";
    public const string With = "Tralys.Ecs.WithAttribute`1";
    public const string Without = "Tralys.Ecs.WithoutAttribute`1";
    public const string AnyOf = "Tralys.Ecs.AnyOfAttribute`1";
    public const string Changed = "Tralys.Ecs.ChangedAttribute`1";
    public const string IncludePendingDestroy = "Tralys.Ecs.IncludePendingDestroyAttribute";
    public const string IncludeDetached = "Tralys.Ecs.IncludeDetachedAttribute";
    public const string Arg = "Tralys.Ecs.ArgAttribute";
    public const string System = "Tralys.Ecs.SystemAttribute";
    public const string View = "Tralys.Ecs.ViewAttribute";
    public const string Part = "Tralys.Ecs.PartAttribute`1";
    public const string World = "Tralys.Ecs.World";
    public const string WorldEntity = "Tralys.Ecs.WorldEntity";
    public const string Optional = "Tralys.Ecs.Optional`1";
    public const string OptionalRef = "Tralys.Ecs.OptionalRef`1";
    public const string ParentTerm = "Tralys.Ecs.Parent`1";
    public const string UpTerm = "Tralys.Ecs.Up`1";
    public const string ChunkContext = "Tralys.Ecs.ChunkContext";
    public const string SystemBase = "Tralys.Ecs.SystemBase";
    public const string Lookup = "Tralys.Ecs.Lookup`1";
    public const string RefLookup = "Tralys.Ecs.RefLookup`1";
    public const string ViewLookup = "Tralys.Ecs.ViewLookup`1";
    public const string IndexLookup = "Tralys.Ecs.IndexLookup`1";
    public const string Reduction = "Tralys.Ecs.IReduction`1";
    public const string SystemSet = "Tralys.Ecs.SystemSetAttribute";
    public const string Structural = "Tralys.Ecs.StructuralAttribute";
    public const string InSet = "Tralys.Ecs.InSetAttribute`1";
    public const string Schedule = "Tralys.Ecs.ScheduleAttribute";
    public const string ScheduleSet = "Tralys.Ecs.SetAttribute`1";
    public const string PlaybackPoint = "Tralys.Ecs.PlaybackPointAttribute";

    public static readonly SymbolDisplayFormat Display =
        SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
            SymbolDisplayGlobalNamespaceStyle.Omitted
        );

    public static string Full(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    public static string Name(ITypeSymbol type) => type.ToDisplayString(Display);

    public static bool Is(ITypeSymbol? type, string metadataName) =>
        type is INamedTypeSymbol named && Matches(named.OriginalDefinition, metadataName);

    public static AttributeData? Attribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().FirstOrDefault(a => IsAttribute(a, metadataName));

    public static bool IsAttribute(AttributeData attribute, string metadataName) =>
        attribute.AttributeClass is { } cls
        && Matches(cls.IsGenericType ? cls.OriginalDefinition : cls, metadataName);

    static bool Matches(INamedTypeSymbol definition, string metadataName)
    {
        var dot = metadataName.LastIndexOf('.');
        var name = definition.MetadataName;
        if (
            name.Length != metadataName.Length - dot - 1
            || string.CompareOrdinal(name, 0, metadataName, dot + 1, name.Length) != 0
        )
            return false;
        var space = definition.ContainingNamespace?.ToDisplayString();
        return space is not null
            && space.Length == dot
            && string.CompareOrdinal(space, 0, metadataName, 0, dot) == 0;
    }

    static readonly string[] IndexAttributes =
    [
        "Tralys.Ecs.UniqueIndexAttribute",
        "Tralys.Ecs.MultiIndexAttribute",
        "Tralys.Ecs.OrderedIndexAttribute",
    ];

    public static int? IndexKindOf(AttributeData attribute)
    {
        for (var kind = 0; kind < IndexAttributes.Length; kind++)
        {
            if (IsAttribute(attribute, IndexAttributes[kind]))
                return kind;
        }
        return null;
    }

    public static T Named<T>(AttributeData attribute, string name, T fallback)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (pair.Key == name && pair.Value.Value is T value)
                return value;
        }
        return fallback;
    }

    public static bool IsDeclared(ITypeSymbol type) =>
        type.GetAttributes()
            .Any(a =>
                IsAttribute(a, Component)
                || IsAttribute(a, Tag)
                || IsAttribute(a, Relation)
                || IsAttribute(a, Rows)
            );

    public static bool Has(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(a => IsAttribute(a, metadataName));

    /// <summary>What a query, view or system needs to know about a declared component or tag; null for any other type.</summary>
    static bool HoldsBuffer(ITypeSymbol type, int depth)
    {
        if (depth > 8)
            return false;
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic)
                continue;
            if (Is(field.Type, "Tralys.Ecs.BufferHandle`1"))
                return true;
            if (
                field.Type
                    is INamedTypeSymbol
                    {
                        TypeKind: TypeKind.Struct,
                        SpecialType: SpecialType.None
                    } inner
                && HoldsBuffer(inner, depth + 1)
            )
                return true;
        }
        return false;
    }

    public static ComponentFacts? Facts(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return null;
        var definition = named.OriginalDefinition;
        var facts = new ComponentFacts();
        var declared = false;
        foreach (var attribute in definition.GetAttributes())
        {
            if (IsAttribute(attribute, Component))
                declared = true;
            else if (IsAttribute(attribute, Tag))
            {
                declared = true;
                facts.IsTag = true;
                var kind = Named(attribute, "Kind", 0);
                facts.IsEnableable |= kind == 1;
                facts.IsSparse |= kind == 2;
            }
            else if (IsAttribute(attribute, "Tralys.Ecs.StorageAttribute"))
                facts.IsSparse |= attribute.ConstructorArguments.FirstOrDefault().Value is 1;
            else if (IsAttribute(attribute, "Tralys.Ecs.EnableableAttribute"))
                facts.IsEnableable = true;
            else if (IsAttribute(attribute, "Tralys.Ecs.ReplaceOnlyAttribute"))
                facts.IsReplaceOnly = true;
            else if (IndexKindOf(attribute) is not null)
                facts.IsIndexed = true;
            else if (IsAttribute(attribute, "Tralys.Ecs.PropagateChangesAttribute"))
                facts.PropagatesChanges = true;
        }
        facts.OwnsBuffers = declared && HoldsBuffer(definition, 0);
        return declared ? facts : null;
    }

    public static bool Implements(ITypeSymbol type, string metadataName) =>
        type.AllInterfaces.Any(i => Is(i, metadataName));

    public static bool DerivesFrom(ITypeSymbol type, string metadataName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (Is(current, metadataName))
                return true;
        }
        return false;
    }

    /// <summary>The narrowest of <paramref name="access"/> and what <paramref name="type"/> and its type arguments allow generated code in
    /// the same assembly; null when generated code cannot name it.</summary>
    public static string? Visibility(ITypeSymbol type, string? access)
    {
        if (access is null)
            return null;
        if (type is IArrayTypeSymbol array)
            return Visibility(array.ElementType, access);
        if (type is not INamedTypeSymbol named)
            return access;
        for (
            INamedTypeSymbol? current = named;
            current is not null;
            current = current.ContainingType
        )
        {
            switch (current.DeclaredAccessibility)
            {
                case Microsoft.CodeAnalysis.Accessibility.Public:
                    break;
                case Microsoft.CodeAnalysis.Accessibility.Internal
                or Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal:
                    access = "internal";
                    break;
                default:
                    return null;
            }
        }
        foreach (var argument in named.TypeArguments)
            access = Visibility(argument, access);
        return access;
    }

    public static bool InGenericType(INamedTypeSymbol type)
    {
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            if (outer.TypeParameters.Length > 0)
                return true;
        }
        return false;
    }

    /// <summary>The generated accessor for a rows type: <c>EffectRows</c> for <c>Effect</c>, <c>StatusEffectRows</c> for <c>StatusEffectRow</c>.</summary>
    public static string RowsAccessor(string typeName) =>
        typeName.EndsWith("Row", StringComparison.Ordinal) ? typeName + "s" : typeName + "Rows";

    public static string AccessibilityKeyword(Accessibility accessibility) =>
        accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
            _ => "private",
        };

    public static string Keyword(TypeDeclarationSyntax syntax)
    {
        var modifiers = string.Join(
            " ",
            syntax
                .Modifiers.Where(m =>
                    m.IsKind(SyntaxKind.ReadOnlyKeyword) || m.IsKind(SyntaxKind.RefKeyword)
                )
                .Select(m => m.Text)
        );
        var keyword = syntax switch
        {
            RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(
                SyntaxKind.StructKeyword
            )
                ? "record struct"
                : "record",
            StructDeclarationSyntax => "struct",
            ClassDeclarationSyntax => "class",
            InterfaceDeclarationSyntax => "interface",
            _ => "struct",
        };
        return modifiers.Length == 0 ? keyword : modifiers + " " + keyword;
    }

    /// <summary>A type header with <c>partial</c> where C# requires it: after modifiers such as <c>readonly</c>, before the type kind.</summary>
    public static string Partial(string keyword)
    {
        var padded = " " + keyword + " ";
        foreach (var kind in new[] { "record", "struct", "class", "interface" })
        {
            var at = padded.IndexOf(" " + kind + " ", StringComparison.Ordinal);
            if (at >= 0)
                return keyword.Insert(at, "partial ");
        }
        return "partial " + keyword;
    }

    public static bool IsPartial(INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.Any(r =>
            r.GetSyntax() is TypeDeclarationSyntax syntax
            && syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))
        );

    public static string DeclarationName(INamedTypeSymbol type) =>
        type.TypeParameters.Length == 0
            ? type.Name
            : type.Name + "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">";

    public static (
        EquatableArray<ContainerModel> Containers,
        INamedTypeSymbol? NotPartial
    ) Containers(INamedTypeSymbol type)
    {
        var chain = new List<ContainerModel>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            if (!IsPartial(outer))
                return (default, outer);
            var keyword = outer.TypeKind switch
            {
                TypeKind.Struct => outer.IsRecord ? "record struct" : "struct",
                TypeKind.Interface => "interface",
                _ => outer.IsRecord ? "record" : "class",
            };
            chain.Insert(0, new ContainerModel(keyword, DeclarationName(outer)));
        }
        return (new EquatableArray<ContainerModel>([.. chain]), null);
    }

    public static string Namespace(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";

    public static string Literal(TypedConstant constant)
    {
        if (constant.IsNull)
            return "null";
        if (constant.Kind == TypedConstantKind.Array)
            return "new object?[] { " + string.Join(", ", constant.Values.Select(Literal)) + " }";
        if (constant.Kind == TypedConstantKind.Type && constant.Value is ITypeSymbol type)
            return "typeof(" + Full(type) + ")";
        if (constant.Kind == TypedConstantKind.Enum && constant.Type is not null)
            return "(" + Full(constant.Type) + ")" + Convert.ToInt64(constant.Value);
        return constant.ToCSharpString();
    }
}

sealed class ComponentFacts
{
    public bool IsTag;
    public bool IsSparse;
    public bool IsEnableable;
    public bool IsReplaceOnly;
    public bool IsIndexed;
    public bool PropagatesChanges;
    public bool OwnsBuffers;

    public bool WritesOnlyStamp => !IsIndexed && !PropagatesChanges && !OwnsBuffers;
}
