using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tralys.Ecs.Generators.Bundles;
using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;
using Tralys.Ecs.Generators.Queries;
using Tralys.Ecs.Generators.Schedules;
using Tralys.Ecs.Generators.Systems;
using Tralys.Ecs.Generators.Views;

namespace Tralys.Ecs.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class EcsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var components = Declarations(context, Symbols.Component, DeclKind.Component);
        var tags = Declarations(context, Symbols.Tag, DeclKind.Tag);
        var relations = Declarations(context, Symbols.Relation, DeclKind.Relation);
        var rows = Declarations(context, Symbols.Rows, DeclKind.Rows);

        foreach (var provider in new[] { components, tags, relations, rows })
            context.RegisterSourceOutput(provider, EmitType);

        var instances = context.CompilationProvider.Select(
            static (compilation, _) =>
                new EquatableArray<InstanceModel>([
                    .. compilation
                        .Assembly.GetAttributes()
                        .Where(a => Symbols.IsAttribute(a, Symbols.Instance))
                        .Select(a => ComponentModelBuilder.Instance(compilation, a))
                        .Where(i => i is not null)
                        .Select(i => i!),
                ])
        );
        var assembly = context.CompilationProvider.Select(
            static (c, _) => c.AssemblyName ?? "Assembly"
        );

        var declared = components
            .Collect()
            .Combine(tags.Collect())
            .Combine(relations.Collect())
            .Combine(rows.Collect())
            .Select(
                static (all, _) =>
                    new EquatableArray<TypeModel>([
                        .. all.Left.Left.Left,
                        .. all.Left.Left.Right,
                        .. all.Left.Right,
                        .. all.Right,
                    ])
            );

        var referenced = context.CompilationProvider.Select(
            static (c, _) =>
                new EquatableArray<string>([
                    .. c
                        .SourceModule.ReferencedAssemblySymbols.Where(a =>
                            a.GetAttributes()
                                .Any(x =>
                                    Symbols.IsAttribute(
                                        x,
                                        "Tralys.Ecs.Generated.ComponentModuleManifestAttribute"
                                    )
                                )
                        )
                        .Select(a => ModuleEmitter.Namespace(a.Name))
                        .OrderBy(n => n, StringComparer.Ordinal),
                ])
        );
        context.RegisterSourceOutput(
            declared.Combine(instances).Combine(assembly).Combine(referenced),
            EmitModule
        );

        var bundles = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                Symbols.Bundle,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => BundleEmitter.Build(ctx)
            )
            .Where(static b => b is not null)
            .Select(static (b, _) => b!);
        context.RegisterSourceOutput(bundles, EmitBundle);

        foreach (
            var (attribute, collect) in new[] { (Symbols.Query, false), (Symbols.Collect, true) }
        )
        {
            var queries = context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    attribute,
                    static (node, _) => node is MethodDeclarationSyntax,
                    (ctx, _) => QueryModelBuilder.Build(ctx, collect)
                )
                .Where(static q => q is not null)
                .Select(static (q, _) => q!);
            context.RegisterSourceOutput(queries, EmitQuery);
        }

        var systems = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                Symbols.System,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => SystemModelBuilder.Build(ctx)
            )
            .Where(static s => s is not null)
            .Select(static (s, _) => s!);
        context.RegisterSourceOutput(systems, EmitSystem);
        var allSystems = systems
            .Collect()
            .Select(static (all, _) => new EquatableArray<SystemModel>([.. all]));

        var schedules = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                Symbols.Schedule,
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, _) => ScheduleEmitter.Build(ctx)
            )
            .Where(static s => s is not null)
            .Select(static (s, _) => s!);
        context.RegisterSourceOutput(
            schedules.Combine(allSystems),
            static (ctx, pair) => ScheduleEmitter.Emit(ctx, pair.Left, pair.Right.Items)
        );

        var views = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                Symbols.View,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => ViewEmitter.Build(ctx)
            )
            .Where(static v => v is not null)
            .Select(static (v, _) => v!);
        context.RegisterSourceOutput(views, EmitView);
    }

    static void EmitQuery(SourceProductionContext context, QueryModel model)
    {
        foreach (var problem in model.Problems.Items)
            problem.Report(context);
        if (model.CanEmit)
            context.AddSource(QueryEmitter.HintName(model), QueryEmitter.Emit(model));
    }

    static void EmitSystem(SourceProductionContext context, SystemModel model)
    {
        foreach (var problem in model.Problems.Items)
            problem.Report(context);
        if (model.Problems.Items.IsEmpty)
            context.AddSource(model.TypeHint + ".System.g.cs", SystemEmitter.Emit(model));
    }

    static void EmitView(SourceProductionContext context, ViewModel model)
    {
        foreach (var problem in model.Problems.Items)
            problem.Report(context);
        if (model.Problems.Items.IsEmpty)
            context.AddSource(model.TypeHint + ".View.g.cs", ViewEmitter.Emit(model));
    }

    static IncrementalValuesProvider<TypeModel> Declarations(
        IncrementalGeneratorInitializationContext context,
        string attribute,
        DeclKind kind
    ) =>
        context
            .SyntaxProvider.ForAttributeWithMetadataName(
                attribute,
                static (node, _) => node is TypeDeclarationSyntax,
                (ctx, _) => ComponentModelBuilder.Build(ctx, kind)
            )
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

    static void EmitType(SourceProductionContext context, TypeModel model)
    {
        foreach (var problem in model.Problems.Items)
            problem.Report(context);
        if (model.CanEmit)
            context.AddSource(
                ModuleEmitter.HintName(model.DisplayName) + ".Ecs.g.cs",
                ModuleEmitter.Type(model)
            );
    }

    static void EmitModule(
        SourceProductionContext context,
        (
            (
                (EquatableArray<TypeModel> Types, EquatableArray<InstanceModel> Instances) Declared,
                string Assembly
            ) Own,
            EquatableArray<string> Referenced
        ) all
    )
    {
        var input = all.Own;
        var types = input
            .Declared.Types.Items.Where(t => t.CanEmit && !t.IsGenericDefinition)
            .OrderBy(t => t.DisplayName, StringComparer.Ordinal)
            .ToList();
        var instances = new List<InstanceModel>();
        foreach (var instance in input.Declared.Instances.Items)
        {
            foreach (var problem in instance.Problems.Items)
                problem.Report(context);
            if (instance.Problems.Items.IsEmpty && instance.Definition is not null)
                instances.Add(instance);
        }
        if (types.Count == 0 && instances.Count == 0 && all.Referenced.Items.IsEmpty)
            return;
        context.AddSource(
            "EcsModule.g.cs",
            ModuleEmitter.Module(input.Assembly, types, instances, all.Referenced)
        );
    }

    static void EmitBundle(SourceProductionContext context, BundleModel model)
    {
        foreach (var problem in model.Problems.Items)
            problem.Report(context);
        if (model.Problems.Items.IsEmpty)
            context.AddSource(
                ModuleEmitter.HintName(model.FullName.Replace("global::", "")) + ".Bundle.g.cs",
                BundleEmitter.Emit(model)
            );
    }
}
