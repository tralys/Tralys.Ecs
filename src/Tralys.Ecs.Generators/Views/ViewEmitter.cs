using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators.Views;

sealed record ViewPart(
    string Type,
    string Name,
    bool ReadOnly,
    bool Optional,
    bool IsTag,
    bool Mutable
);

sealed record ViewModel(
    string Namespace,
    EquatableArray<ContainerModel> Containers,
    string Keyword,
    string DeclarationName,
    string Name,
    string TypeHint,
    EquatableArray<ViewPart> Parts,
    EquatableArray<Problem> Problems
);

static class ViewEmitter
{
    const string Ecs = "global::Tralys.Ecs.";

    public static ViewModel? Build(GeneratorAttributeSyntaxContext context)
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
        var parts = new List<ViewPart>();
        foreach (var attribute in type.GetAttributes())
        {
            if (
                !Symbols.IsAttribute(attribute, Symbols.Part)
                || attribute.AttributeClass is not { TypeArguments.Length: 1 } partAttribute
            )
                continue;
            var part = partAttribute.TypeArguments[0];
            if (Symbols.Facts(part) is not { } facts)
                continue;
            if (parts.Any(p => p.Name == part.Name))
            {
                problems.Add(
                    new Problem(
                        Diagnostics.RepeatedViewPart,
                        LocationInfo.From(
                            attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                        ) ?? location,
                        Symbols.Name(part),
                        Symbols.Name(type)
                    )
                );
                continue;
            }
            parts.Add(
                new ViewPart(
                    Symbols.Full(part),
                    part.Name,
                    Symbols.Named(attribute, "ReadOnly", false),
                    Symbols.Named(attribute, "Optional", false),
                    facts.IsTag,
                    !facts.IsTag && !facts.IsReplaceOnly
                )
            );
        }
        return new ViewModel(
            Symbols.Namespace(type),
            containers,
            Symbols.Keyword(syntax),
            Symbols.DeclarationName(type),
            type.Name,
            ModuleEmitter.HintName(Symbols.Name(type)),
            new([.. parts]),
            new([.. problems])
        );
    }

    public static string Emit(ViewModel model)
    {
        var writer = new CodeWriter();
        writer.Line(ModuleEmitter.Header);
        if (model.Namespace.Length > 0)
            writer.Line($"namespace {model.Namespace};");
        writer.Line();
        var scopes = new List<CodeWriter.Scope>();
        foreach (var container in model.Containers.Items)
            scopes.Add(writer.Block($"partial {container.Keyword} {container.Name}"));

        var self = model.DeclarationName;
        using (writer.Block($"{Symbols.Partial(model.Keyword)} {self} : {Ecs}IEntityView<{self}>"))
        {
            writer.Line($"readonly {Ecs}WorldEntity __entity;");
            writer.Line();
            writer.Line($"{model.Name}({Ecs}WorldEntity entity) => __entity = entity;");
            writer.Line();
            writer.Line($"public {Ecs}WorldEntity Entity => __entity;");
            writer.Line();
            writer.Line("public bool IsLive => __entity.IsLive;");
            writer.Line();
            using (
                writer.Block(
                    $"public static bool TryFrom({Ecs}WorldEntity entity, out {self} view)"
                )
            )
            {
                var checks = model
                    .Parts.Items.Where(p => !p.Optional)
                    .Select(p => $" && entity.Has<{p.Type}>()");
                using (
                    writer.Block(
                        $"if (entity.State is not {Ecs}LifecycleState.Dead{string.Concat(checks)})"
                    )
                )
                {
                    writer.Line($"view = new {self}(entity);");
                    writer.Line("return true;");
                }
                writer.Line("view = default;");
                writer.Line("return false;");
            }
            foreach (var part in model.Parts.Items)
            {
                writer.Line();
                Part(writer, part);
            }
        }

        for (var i = scopes.Count - 1; i >= 0; i--)
            scopes[i].Dispose();
        return writer.ToString();
    }

    // Without effects, TECS0045 and TECS0017 cannot see a view's structural writes.
    const string StructuralEffect =
        "[global::Tralys.Ecs.Generated.Effect(global::Tralys.Ecs.Generated.Effects.Structural | global::Tralys.Ecs.Generated.Effects.MovesRows | global::Tralys.Ecs.Generated.Effects.Mutates)]";

    const string WriteEffect =
        "[global::Tralys.Ecs.Generated.Effect(global::Tralys.Ecs.Generated.Effects.Mutates)]";

    const string CallerInfo =
        "[global::System.Runtime.CompilerServices.CallerFilePath] string file = \"\", "
        + "[global::System.Runtime.CompilerServices.CallerLineNumber] int line = 0";

    static void Part(CodeWriter writer, ViewPart part)
    {
        if (part.IsTag)
        {
            if (!part.Optional)
                return;
            writer.Line($"public bool {part.Name} => __entity.Has<{part.Type}>();");
            if (part.ReadOnly)
                return;
            writer.Line();
            writer.Line(StructuralEffect);
            writer.Line(
                $"public void Add{part.Name}({CallerInfo}) => __entity.Add<{part.Type}>(file: file, line: line);"
            );
            writer.Line();
            writer.Line(StructuralEffect);
            writer.Line(
                $"public bool Remove{part.Name}({CallerInfo}) => __entity.Remove<{part.Type}>(file: file, line: line);"
            );
            return;
        }

        writer.Line(
            $"public bool TryGet{part.Name}(out {part.Type} value) => __entity.TryGet<{part.Type}>(out value);"
        );
        writer.Line();
        if (part.Optional)
        {
            writer.Line(
                $"public {part.Type}? {part.Name} => TryGet{part.Name}(out var value) ? value : null;"
            );
            if (part.ReadOnly)
                return;
            writer.Line();
            writer.Line(StructuralEffect);
            writer.Line(
                $"public void SetOrRemove{part.Name}(in {part.Type}? value, {CallerInfo}) => __entity.SetOrRemove(value, file: file, line: line);"
            );
            return;
        }
        writer.Line(
            $"public ref readonly {part.Type} {part.Name} => ref __entity.Get<{part.Type}>();"
        );
        if (part.ReadOnly)
            return;
        writer.Line();
        writer.Line(WriteEffect);
        writer.Line(
            $"public void Replace{part.Name}(in {part.Type} value, {CallerInfo}) => __entity.Replace(value, file: file, line: line);"
        );
        if (!part.Mutable)
            return;
        writer.Line();
        writer.Line(WriteEffect);
        writer.Line(
            $"public {Ecs}ComponentEdit<{part.Type}> Edit{part.Name}() => __entity.Edit<{part.Type}>();"
        );
    }
}
