using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators.Bundles;

static class BundleEmitter
{
    public static BundleModel? Build(GeneratorAttributeSyntaxContext context)
    {
        if (
            context.TargetSymbol is not INamedTypeSymbol type
            || context.TargetNode is not TypeDeclarationSyntax syntax
        )
            return null;
        var problems = new List<Problem>();
        var location = LocationInfo.From(type.Locations.FirstOrDefault());
        var (containers, notPartial) = Symbols.Containers(type);
        if (!Symbols.IsPartial(type) || notPartial is not null)
            problems.Add(
                new Problem(Diagnostics.NotPartial, location, Symbols.Name(notPartial ?? type))
            );

        var compilation = context.SemanticModel.Compilation;
        var members = new List<BundleMember>();
        var present = new HashSet<string>();
        var requirements = new List<(ITypeSymbol Member, ITypeSymbol Required)>();
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.AssociatedSymbol is not null)
                continue;
            if (!Symbols.IsDeclared(field.Type))
            {
                problems.Add(
                    new Problem(
                        Diagnostics.BundleFieldNotComponent,
                        LocationInfo.From(field.Locations.FirstOrDefault()),
                        field.Name,
                        Symbols.Name(type),
                        Symbols.Name(field.Type)
                    )
                );
                continue;
            }
            var isTag = field.Type.GetAttributes().Any(a => Symbols.IsAttribute(a, Symbols.Tag));
            members.Add(new BundleMember(field.Name, Symbols.Full(field.Type), isTag));
            present.Add(Symbols.Name(field.Type));
            foreach (var attribute in field.Type.GetAttributes())
            {
                if (
                    Symbols.IsAttribute(attribute, Symbols.Requires)
                    && attribute.AttributeClass is { TypeArguments.Length: 1 } requirement
                )
                    requirements.Add((field.Type, requirement.TypeArguments[0]));
            }
        }
        foreach (var (member, required) in requirements)
        {
            if (!present.Contains(Symbols.Name(required)))
                problems.Add(
                    new Problem(
                        Diagnostics.BundleMissingRequirement,
                        location,
                        Symbols.Name(type),
                        Symbols.Name(member),
                        Symbols.Name(required)
                    )
                );
        }

        return new BundleModel(
            Symbols.Namespace(type),
            containers,
            Symbols.DeclarationName(type),
            Symbols.Keyword(syntax),
            Symbols.Full(type),
            new([.. members]),
            new([.. problems])
        );
    }

    public static string Emit(BundleModel model)
    {
        var writer = new CodeWriter();
        writer.Line(ModuleEmitter.Header);
        if (model.Namespace.Length > 0)
            writer.Line($"namespace {model.Namespace};");
        writer.Line();
        var scopes = new List<CodeWriter.Scope>();
        foreach (var container in model.Containers.Items)
            scopes.Add(writer.Block($"partial {container.Keyword} {container.Name}"));

        using (
            writer.Block(
                $"{Symbols.Partial(model.Keyword)} {model.DeclarationName} : global::Tralys.Ecs.IBundle"
            )
        )
        {
            using (
                writer.Block(
                    "static void global::Tralys.Ecs.IBundle.Describe(ref global::Tralys.Ecs.Generated.BundleShape shape)"
                )
            )
            {
                foreach (var member in model.Members.Items)
                    writer.Line($"shape.Add<{member.Type}>();");
            }
            writer.Line();
            using (
                writer.Block(
                    "readonly void global::Tralys.Ecs.IBundle.Write(ref global::Tralys.Ecs.Generated.BundleWriter writer)"
                )
            )
            {
                foreach (var member in model.Members.Items)
                {
                    if (!member.IsTag)
                        writer.Line($"writer.Write(in {member.Field});");
                }
            }
        }
        for (var i = scopes.Count - 1; i >= 0; i--)
            scopes[i].Dispose();
        return writer.ToString();
    }
}
