using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Indexes;

namespace Tralys.Ecs.Generators.Metadata;

static class ComponentModelBuilder
{
    public static TypeModel? Build(GeneratorAttributeSyntaxContext context, DeclKind kind)
    {
        if (
            context.TargetSymbol is not INamedTypeSymbol type
            || context.TargetNode is not TypeDeclarationSyntax syntax
            || type.GetAttributes().Select(KindOf).FirstOrDefault(k => k is not null) != kind
        )
            return null;
        return Build(
            context.SemanticModel.Compilation,
            type,
            Symbols.Keyword(syntax),
            context.Attributes[0],
            kind
        );
    }

    static DeclKind? KindOf(AttributeData attribute) =>
        Symbols.IsAttribute(attribute, Symbols.Component) ? DeclKind.Component
        : Symbols.IsAttribute(attribute, Symbols.Tag) ? DeclKind.Tag
        : Symbols.IsAttribute(attribute, Symbols.Relation) ? DeclKind.Relation
        : Symbols.IsAttribute(attribute, Symbols.Rows) ? DeclKind.Rows
        : null;

    public static TypeModel Build(
        Compilation compilation,
        INamedTypeSymbol type,
        string keyword,
        AttributeData declaring,
        DeclKind kind
    )
    {
        var problems = new List<Problem>();
        var location = LocationInfo.From(type.Locations.FirstOrDefault());
        var (containers, notPartial) = Symbols.Containers(type);
        if (!Symbols.IsPartial(type) || notPartial is not null)
            problems.Add(
                new Problem(Diagnostics.NotPartial, location, Symbols.Name(notPartial ?? type))
            );

        var generic = type.TypeParameters.Length > 0;
        if (type.GetAttributes().Count(a => KindOf(a) is not null) > 1)
            problems.Add(
                new Problem(Diagnostics.MultipleDeclarations, location, Symbols.Name(type))
            );

        var traits = 0;
        var storage = 0;
        var relationTraits = 0;
        var onTargetDestroyed = 0;
        ulong categories = 0;
        var aliases = new List<string>();
        var requires = new List<string>();
        var indexAttributes = new List<AttributeData>();
        var passthrough = new List<PassthroughModel>();

        if (kind == DeclKind.Tag && Symbols.Named(declaring, "Inherited", false))
            traits |= Traits.InheritedTag;
        if (kind == DeclKind.Rows)
        {
            if (Symbols.Named(declaring, "PresenceMask", false))
                traits |= Traits.PresenceMask;
            if (Symbols.Named(declaring, "ReverseKeyIndex", false))
                traits |= Traits.ReverseKeyIndex;
        }
        if (kind == DeclKind.Relation)
        {
            relationTraits = Symbols.Named(declaring, "Traits", 0);
            onTargetDestroyed = Symbols.Named(declaring, "OnTargetDestroyed", 0);
        }

        foreach (var attribute in type.GetAttributes())
        {
            if (Symbols.IsAttribute(attribute, "Tralys.Ecs.UntrackedAttribute"))
                traits |= Traits.Untracked;
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.EnableableAttribute"))
                traits |= Traits.Enableable;
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.ReplaceOnlyAttribute"))
                traits |= Traits.ReplaceOnly;
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.SingletonAttribute"))
                traits |= Traits.Singleton;
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.PropagateChangesAttribute"))
            {
                if (Symbols.Named(attribute, "ToAncestors", false))
                    traits |= Traits.PropagateToAncestors;
                if (Symbols.Named(attribute, "ToRelationSources", false))
                    traits |= Traits.PropagateToRelationSources;
            }
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.StorageAttribute"))
                storage = attribute.ConstructorArguments.FirstOrDefault().Value is int value
                    ? value
                    : 0;
            else if (Symbols.IsAttribute(attribute, "Tralys.Ecs.AliasAttribute"))
            {
                if (attribute.ConstructorArguments.FirstOrDefault().Value is string alias)
                    aliases.Add(alias);
            }
            else if (Symbols.IsAttribute(attribute, Symbols.Requires))
            {
                var required = attribute.AttributeClass!.TypeArguments[0];
                requires.Add(Symbols.Name(required));
            }
            else if (Symbols.IndexKindOf(attribute) is not null)
                indexAttributes.Add(attribute);
            else if (Passthrough(attribute) is { } use)
            {
                passthrough.Add(use.Model);
                if (use.CategoryBit is { } bit && bit is >= 0 and < 64)
                    categories |= 1UL << bit;
            }
        }

        var fields = kind == DeclKind.Tag ? [] : Fields(type);
        var walker = new LayoutWalker(compilation, type);
        if (kind != DeclKind.Tag)
            walker.Walk();
        problems.AddRange(walker.Problems);

        var indexes = new List<IndexModel>();
        var accessorVisibility =
            indexAttributes.Count == 0
                ? ""
                : IndexModelBuilder.Build(type, kind, generic, indexAttributes, indexes, problems);

        string? keyField = null;
        string? keyType = null;
        if (kind == DeclKind.Rows)
        {
            var key = type.GetMembers()
                .OfType<IFieldSymbol>()
                .FirstOrDefault(f =>
                    f.GetAttributes().Any(a => Symbols.IsAttribute(a, "Tralys.Ecs.RowKeyAttribute"))
                );
            keyField = key?.Name;
            keyType = key is null ? null : Symbols.Full(key.Type);
            if (key is null)
                problems.Add(new Problem(Diagnostics.RowsWithoutKey, location, Symbols.Name(type)));
            else if (
                (traits & Traits.PresenceMask) != 0
                && PresenceMaskProblem(key.Type) is { } why
            )
                problems.Add(
                    new Problem(Diagnostics.PresenceMaskKey, location, Symbols.Name(type), why)
                );
            if (key is not null && !generic && !Symbols.InGenericType(type))
                accessorVisibility =
                    Symbols.Visibility(key.Type, Symbols.Visibility(type, "public")) ?? "";
        }

        return new TypeModel(
            Symbols.Namespace(type),
            containers,
            Symbols.DeclarationName(type),
            keyword,
            Symbols.Full(type),
            Symbols.Name(type),
            generic,
            kind,
            Symbols.Named(declaring, "SchemaVersion", 1),
            traits,
            storage,
            relationTraits,
            onTargetDestroyed,
            categories,
            new([.. aliases]),
            new([.. requires]),
            new([.. fields]),
            new([.. walker.EntityRefs]),
            new([.. walker.BufferHandles]),
            new([.. indexes]),
            accessorVisibility,
            new([.. passthrough]),
            keyField,
            keyType,
            LayoutKey.Of(type),
            new([.. problems])
        );
    }

    static string? PresenceMaskProblem(ITypeSymbol key)
    {
        if (key is ITypeParameterSymbol)
            return null;
        if (key is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration)
        {
            foreach (var member in enumeration.GetMembers().OfType<IFieldSymbol>())
            {
                if (
                    member.HasConstantValue
                    && Convert.ToDecimal(member.ConstantValue) is < 0 or > 63
                )
                    return $"'{member.Name}' is {member.ConstantValue}";
            }
            return null;
        }
        return
            key.SpecialType
                is SpecialType.System_Byte
                    or SpecialType.System_SByte
                    or SpecialType.System_Int16
                    or SpecialType.System_UInt16
                    or SpecialType.System_Int32
                    or SpecialType.System_UInt32
                    or SpecialType.System_Int64
                    or SpecialType.System_UInt64
            ? null
            : $"'{Symbols.Name(key)}' is neither";
    }

    static List<FieldModel> Fields(INamedTypeSymbol type)
    {
        var fields = new List<FieldModel>();
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (
                field.IsStatic
                || field.IsConst
                || field.AssociatedSymbol is not null
                || field.IsFixedSizeBuffer
            )
                continue;
            var capture = 0;
            var uses = new List<PassthroughModel>();
            foreach (var attribute in field.GetAttributes())
            {
                if (Symbols.IsAttribute(attribute, "Tralys.Ecs.CaptureAttribute"))
                    capture = attribute.ConstructorArguments.FirstOrDefault().Value is int c
                        ? c
                        : 0;
                else if (Passthrough(attribute) is { } use)
                    uses.Add(use.Model);
            }
            fields.Add(
                new FieldModel(
                    field.Name,
                    "v." + field.Name,
                    field.Type is ITypeParameterSymbol p ? p.Name : Symbols.Full(field.Type),
                    capture,
                    new([.. uses])
                )
            );
        }
        return fields;
    }

    static (PassthroughModel Model, int? CategoryBit)? Passthrough(AttributeData attribute)
    {
        var cls = attribute.AttributeClass;
        if (cls is null)
            return null;
        int? categoryBit = null;
        var recorded = false;
        foreach (var meta in cls.GetAttributes())
        {
            if (Symbols.IsAttribute(meta, "Tralys.Ecs.ComponentCategoryAttribute"))
            {
                categoryBit = Symbols.Named(meta, "Bit", 0);
                recorded = true;
            }
            else if (Symbols.IsAttribute(meta, "Tralys.Ecs.MetadataPassthroughAttribute"))
                recorded = true;
        }
        if (!recorded)
            return null;

        var arguments = new List<string>();
        var names = new List<string>();
        foreach (var argument in attribute.ConstructorArguments)
        {
            arguments.Add(Symbols.Literal(argument));
            names.Add("");
        }
        foreach (var pair in attribute.NamedArguments)
        {
            arguments.Add(Symbols.Literal(pair.Value));
            names.Add(pair.Key);
        }
        return (
            new PassthroughModel(Symbols.Name(cls), new([.. arguments]), new([.. names])),
            categoryBit
        );
    }

    public static InstanceModel? Instance(Compilation compilation, AttributeData attribute)
    {
        if (attribute.AttributeClass?.TypeArguments.FirstOrDefault() is not INamedTypeSymbol closed)
            return null;
        var problems = new List<Problem>();
        var location = LocationInfo.From(
            attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
        );
        var open = closed.OriginalDefinition;
        var declaring = open.GetAttributes().FirstOrDefault(a => KindOf(a) is not null);
        var definition = declaring is null
            ? null
            : Build(compilation, open, "struct", declaring, KindOf(declaring)!.Value) with
            {
                KeyType = ClosedKeyType(closed),
                Problems = default,
            };

        var walker = new LayoutWalker(compilation, closed, compilation.Assembly);
        if (definition is { Kind: not DeclKind.Tag })
            walker.Walk();
        problems.AddRange(walker.Problems);

        return new InstanceModel(
            Symbols.Full(closed),
            Symbols.Name(closed),
            Symbols.Named(attribute, "SchemaVersion", 1),
            definition,
            new([.. walker.EntityRefs]),
            new([.. walker.BufferHandles]),
            LayoutKey.Of(closed),
            new([.. problems])
        );
    }

    static string? ClosedKeyType(INamedTypeSymbol closed) =>
        closed
            .GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(f =>
                f.GetAttributes().Any(a => Symbols.IsAttribute(a, "Tralys.Ecs.RowKeyAttribute"))
            )
            ?.Type
            is { } key
            ? Symbols.Full(key)
            : null;
}
