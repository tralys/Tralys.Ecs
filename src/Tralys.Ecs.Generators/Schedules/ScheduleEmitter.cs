using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;
using Tralys.Ecs.Generators.Systems;

namespace Tralys.Ecs.Generators.Schedules;

sealed record PointModel(
    string Name,
    string? After,
    string? AfterName,
    string? Before,
    string? BeforeName,
    bool FinalizesDestroys
);

sealed record ScheduleModel(
    string Namespace,
    EquatableArray<ContainerModel> Containers,
    string Keyword,
    string TypeName,
    string TypeHint,
    string DisplayName,
    EquatableArray<string> Sets,
    EquatableArray<PointModel> Points,
    LocationInfo? Location,
    EquatableArray<Problem> Problems
);

/// <summary>Checks a <c>[Schedule]</c>'s playback points against its sets and the systems this compilation puts in them, and emits its
/// declaration.</summary>
static class ScheduleEmitter
{
    public static ScheduleModel? Build(GeneratorAttributeSyntaxContext context)
    {
        if (
            context.TargetSymbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } type
            || context.TargetNode is not (ClassDeclarationSyntax or RecordDeclarationSyntax)
        )
            return null;
        var problems = new List<Problem>();
        var location = LocationInfo.From(type.Locations.FirstOrDefault());
        var (containers, notPartial) = Symbols.Containers(type);
        if (!Symbols.IsPartial(type) || notPartial is not null)
            problems.Add(
                new Problem(Diagnostics.NotPartial, location, Symbols.Name(notPartial ?? type))
            );
        var sets = new List<string>();
        var points = new List<PointModel>();
        foreach (var attribute in type.GetAttributes())
        {
            if (
                Symbols.IsAttribute(attribute, Symbols.ScheduleSet)
                && attribute.AttributeClass is { TypeArguments.Length: 1 } setAttribute
            )
            {
                var set = setAttribute.TypeArguments[0];
                if (!Symbols.Has(set, Symbols.SystemSet))
                    problems.Add(
                        new Problem(
                            Diagnostics.InvalidSet,
                            location,
                            Symbols.Name(type),
                            Symbols.Name(set)
                        )
                    );
                sets.Add(Symbols.Full(set));
            }
            else if (Symbols.IsAttribute(attribute, Symbols.PlaybackPoint))
            {
                var name = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "";
                var after = Symbols.Named<ITypeSymbol?>(attribute, "After", null);
                var before = Symbols.Named<ITypeSymbol?>(attribute, "Before", null);
                var finalizes = Symbols.Named(attribute, "FinalizesDestroys", false);
                var problem =
                    points.Exists(p => p.Name == name) ? "is declared twice"
                    : finalizes && points.Exists(p => p.FinalizesDestroys)
                        ? "finalises destroys, and another point already does"
                    : null;
                if (problem is not null)
                    problems.Add(
                        new Problem(
                            Diagnostics.InvalidPlaybackPoint,
                            location,
                            name,
                            Symbols.Name(type),
                            problem
                        )
                    );
                points.Add(
                    new PointModel(
                        name,
                        after is null ? null : Symbols.Full(after),
                        after?.Name,
                        before is null ? null : Symbols.Full(before),
                        before?.Name,
                        finalizes
                    )
                );
            }
        }
        return new ScheduleModel(
            Symbols.Namespace(type),
            containers,
            type.IsRecord ? "record" : "class",
            Symbols.DeclarationName(type),
            ModuleEmitter.HintName(Symbols.Name(type)),
            Symbols.Name(type),
            new([.. sets]),
            new([.. points]),
            location,
            new([.. problems])
        );
    }

    public static void Emit(
        SourceProductionContext context,
        ScheduleModel schedule,
        IEnumerable<SystemModel> all
    )
    {
        foreach (var problem in schedule.Problems.Items)
            problem.Report(context);
        if (!schedule.Problems.Items.IsEmpty)
            return;

        var sets = schedule.Sets.Items;
        var members = all.Where(s => s.Set is { } set && sets.Contains(set))
            .OrderBy(s => s.DisplayName, StringComparer.Ordinal)
            .ToList();
        var valid = true;
        foreach (var point in schedule.Points.Items)
        {
            foreach (
                var (target, name) in new[]
                {
                    (point.After, point.AfterName),
                    (point.Before, point.BeforeName),
                }
            )
            {
                if (
                    target is null
                    || sets.Contains(target)
                    || members.Exists(m => m.FullName == target)
                )
                    continue;
                valid = false;
                new Problem(
                    Diagnostics.InvalidPlaybackPoint,
                    schedule.Location,
                    point.Name,
                    schedule.DisplayName,
                    $"orders against '{name ?? target}', which is neither one of its sets nor a system in them; name a set of the schedule or a system in one"
                ).Report(context);
            }
        }
        if (!valid)
            return;

        var order = members.Where(m => m.Reachable).Select(m => $"typeof({m.FullName})");
        var writer = new CodeWriter();
        writer.Line(ModuleEmitter.Header);
        if (schedule.Namespace.Length > 0)
            writer.Line($"namespace {schedule.Namespace};");
        writer.Line();
        var scopes = new List<CodeWriter.Scope>();
        foreach (var container in schedule.Containers.Items)
            scopes.Add(writer.Block($"partial {container.Keyword} {container.Name}"));
        using (writer.Block($"partial {schedule.Keyword} {schedule.TypeName}"))
        {
            writer.Line(
                "public static global::Tralys.Ecs.ScheduleDeclaration Declaration { get; } ="
            );
            using (writer.Indented())
            {
                writer.Line("new(");
                using (writer.Indented())
                {
                    writer.Line($"\"{schedule.DisplayName}\",");
                    writer.Line("[" + string.Join(", ", sets.Select(s => $"typeof({s})")) + "],");
                    writer.Line(
                        "["
                            + string.Join(
                                ", ",
                                schedule.Points.Items.Select(p =>
                                    $"new global::Tralys.Ecs.PlaybackPointDeclaration({SymbolDisplay.FormatLiteral(p.Name, quote: true)}, [{Typeof(p.After)}], [{Typeof(p.Before)}]{(p.FinalizesDestroys ? ", finalizesDestroys: true" : "")})"
                                )
                            )
                            + "],"
                    );
                    writer.Line("[" + string.Join(", ", order) + "]");
                }
                writer.Line(");");
            }
        }
        for (var i = scopes.Count - 1; i >= 0; i--)
            scopes[i].Dispose();
        context.AddSource(schedule.TypeHint + ".Schedule.g.cs", writer.ToString());
    }

    static string Typeof(string? type) => type is null ? "" : $"typeof({type})";
}
