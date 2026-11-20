using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Tralys.Ecs.Generators.CodeGen;

namespace Tralys.Ecs.Generators.Queries;

static class QueryModelBuilder
{
    public static QueryModel? Build(GeneratorAttributeSyntaxContext context, bool collect)
    {
        if (
            context.TargetSymbol is not IMethodSymbol method
            || context.TargetNode is not MethodDeclarationSyntax
        )
            return null;
        return Parse(method, context.Attributes[0], collect);
    }

    public static QueryModel Parse(IMethodSymbol method, AttributeData attribute, bool collect)
    {
        var problems = new List<Problem>();
        var location = LocationInfo.From(method.Locations.FirstOrDefault());
        var host = method.ContainingType;
        var name = host.Name + "." + method.Name;

        void Fail(string reason) =>
            problems.Add(new Problem(Diagnostics.InvalidQuery, location, name, reason));

        var (containers, notPartial) = Symbols.Containers(host);
        if (host.TypeKind != TypeKind.Class)
            problems.Add(
                new Problem(Diagnostics.InvalidQueryHost, location, name, "its type is not a class")
            );
        else if (!Symbols.IsPartial(host) || notPartial is not null)
            problems.Add(
                new Problem(
                    Diagnostics.InvalidQueryHost,
                    location,
                    name,
                    $"'{Symbols.Name(notPartial ?? host)}' is not partial"
                )
            );
        if (method.TypeParameters.Length > 0)
            problems.Add(
                new Problem(Diagnostics.InvalidQueryHost, location, name, "the method is generic")
            );
        if (
            host.GetMembers(method.Name)
                .OfType<IMethodSymbol>()
                .Count(m => Symbols.Has(m, Symbols.Query) || Symbols.Has(m, Symbols.Collect)) > 1
        )
            Fail("another query in the type has the same name");

        var parallel = !collect && Symbols.Named(attribute, "Parallel", false);
        var depthOrdered = !collect && Symbols.Named(attribute, "DepthOrdered", false);
        if (parallel && depthOrdered)
            Fail(
                "a depth-ordered query runs serially, parent before child, so it cannot be parallel"
            );
        var returnsBool = method.ReturnType.SpecialType == SpecialType.System_Boolean;
        if (collect && !returnsBool)
            Fail("a collect predicate returns bool");
        else if (!returnsBool && !method.ReturnsVoid)
            Fail("the body returns void, or bool to stop the run");
        if (parallel && returnsBool)
            Fail("a parallel query cannot stop early, so its body returns void");

        var parameters = new List<ParamModel>();
        var terms = new List<TermModel>();
        string? accumulator = null;
        foreach (var p in method.Parameters)
        {
            var model = Parameter(p, terms.Count, parallel, out var term, out var problem);
            if (problem is not null)
            {
                problems.Add(
                    new Problem(
                        problem.Value.Descriptor,
                        LocationInfo.From(p.Locations.FirstOrDefault()) ?? location,
                        p.Name,
                        name,
                        problem.Value.Detail
                    )
                );
                continue;
            }
            parameters.Add(model!);
            if (term is not null)
                terms.Add(term);
            if (model!.Kind != ParamKind.Accumulator)
                continue;
            if (accumulator is not null)
                Fail("it takes more than one accumulator");
            else if (collect)
                Fail("a collect predicate cannot reduce");
            accumulator = model.Type;
            if (depthOrdered)
                Fail("a depth-ordered query cannot reduce");
        }

        if (parallel)
        {
            foreach (var read in parameters)
            {
                if (
                    read.Kind is ParamKind.Parent or ParamKind.Up
                    && parameters.Exists(w =>
                        w.Kind is ParamKind.Ref or ParamKind.OptionalRef && w.Type == read.Type
                    )
                )
                    Fail(
                        $"a parallel query cannot read '{read.Type.Replace("global::", "")}' from a parent or ancestor while writing it, since another chunk may be writing that ancestor's value"
                    );
            }
        }

        var includePending = false;
        var includeDetached = false;
        foreach (var use in method.GetAttributes())
        {
            if (Symbols.IsAttribute(use, Symbols.IncludePendingDestroy))
                includePending = true;
            else if (Symbols.IsAttribute(use, Symbols.IncludeDetached))
                includeDetached = true;
            else if (Filter(use) is { } kind)
            {
                var type = use.AttributeClass!.TypeArguments[0];
                var facts = Symbols.Facts(type);
                if (facts is null)
                {
                    Fail($"'{Symbols.Name(type)}' in a filter is not a declared component or tag");
                    continue;
                }
                terms.Add(
                    new TermModel(
                        Symbols.Full(type),
                        kind,
                        kind == TermKind.AnyOf ? Symbols.Named(use, "Group", 0) : -1,
                        facts.IsSparse
                    )
                );
            }
        }

        if (
            !terms.Exists(t =>
                t.Kind
                    is TermKind.Read
                        or TermKind.Write
                        or TermKind.With
                        or TermKind.AnyOf
                        or TermKind.Changed
            )
            && problems.Count == 0
        )
            problems.Add(new Problem(Diagnostics.QueryWithoutPositiveTerm, location, name));

        string? act = null;
        var actStatic = false;
        var actArgs = new List<ActArgModel>();
        if (collect)
        {
            act = Symbols.Named<string?>(attribute, "Act", null);
            Act(host, act, parameters, actArgs, out actStatic, Fail);
        }

        return new QueryModel(
            Symbols.Namespace(host),
            containers,
            host.IsRecord ? "record" : "class",
            Symbols.DeclarationName(host),
            Metadata.ModuleEmitter.HintName(Symbols.Name(host)),
            Symbols.Has(host, Symbols.System),
            method.Name,
            Symbols.AccessibilityKeyword(method.DeclaredAccessibility),
            method.IsStatic,
            parallel,
            depthOrdered,
            returnsBool,
            collect,
            act,
            actStatic,
            new([.. actArgs]),
            accumulator,
            new([.. parameters]),
            new([.. terms]),
            includePending,
            includeDetached,
            new([
                .. Symbols.Has(host, Symbols.System)
                    ? []
                    : host.GetMembers()
                        .OfType<IFieldSymbol>()
                        .Where(f => !f.IsReadOnly && IsLookup(f.Type))
                        .Select(f => new HostLookup(f.Name, f.IsStatic)),
            ]),
            new([.. problems])
        );
    }

    static bool IsLookup(ITypeSymbol type) =>
        Symbols.Is(type, Symbols.Lookup)
        || Symbols.Is(type, Symbols.RefLookup)
        || Symbols.Is(type, Symbols.ViewLookup)
        || Symbols.Is(type, Symbols.IndexLookup);

    static TermKind? Filter(AttributeData attribute) =>
        Symbols.IsAttribute(attribute, Symbols.With) ? TermKind.With
        : Symbols.IsAttribute(attribute, Symbols.Without) ? TermKind.Without
        : Symbols.IsAttribute(attribute, Symbols.AnyOf) ? TermKind.AnyOf
        : Symbols.IsAttribute(attribute, Symbols.Changed) ? TermKind.Changed
        : null;

    static ParamModel? Parameter(
        IParameterSymbol p,
        int nextTerm,
        bool parallel,
        out TermModel? term,
        out Problem? problem
    )
    {
        term = null;
        problem = null;
        var type = p.Type;
        var full = Symbols.Full(type);
        var refStruct = type.IsRefLikeType;

        ParamModel Make(
            ParamKind kind,
            int termIndex = -1,
            bool sparse = false,
            string modifier = "",
            bool stampOnly = false
        ) => new(p.Name, kind, full, termIndex, sparse, modifier, refStruct, stampOnly);

        Problem Invalid(string why) => new(Diagnostics.InvalidQueryParameter, null, "", why);

        if (Symbols.Has(p, Symbols.Arg))
        {
            if (p.RefKind is RefKind.Ref or RefKind.Out)
            {
                problem = Invalid("an [Arg] is passed by value or by in");
                return null;
            }
            if (parallel && refStruct)
            {
                problem = Invalid(
                    "a parallel query cannot carry a ref struct [Arg] to its workers"
                );
                return null;
            }
            return Make(ParamKind.Arg, modifier: p.RefKind == RefKind.In ? "in" : "");
        }
        if (p.RefKind == RefKind.Out)
        {
            problem = Invalid("out parameters are not supported");
            return null;
        }
        if (p.RefKind == RefKind.None)
        {
            if (Symbols.Is(type, Symbols.Entity))
                return Make(ParamKind.Entity);
            if (Symbols.Is(type, Symbols.Commands))
            {
                if (parallel)
                    return Make(ParamKind.Commands);
                problem = Invalid(
                    "only a parallel body records through Commands; a serial body or [Collect] predicate changes no structure, so return true from a [Collect] predicate and make the change in its Act"
                );
                return null;
            }
            if (Symbols.Is(type, Symbols.ChunkContext))
                return Make(ParamKind.Context);
            if (Symbols.Is(type, Symbols.World))
                return Make(ParamKind.World);
        }
        var hierarchy =
            Symbols.Is(type, Symbols.ParentTerm) ? ParamKind.Parent
            : Symbols.Is(type, Symbols.UpTerm) ? ParamKind.Up
            : (ParamKind?)null;
        if (hierarchy is { } hierarchyKind && p.RefKind == RefKind.None)
        {
            var inner = ((INamedTypeSymbol)type).TypeArguments[0];
            if (Symbols.Facts(inner) is not { IsTag: false })
            {
                problem = Invalid(
                    $"'{Symbols.Name(inner)}' is not a declared component with a value"
                );
                return null;
            }
            return new ParamModel(
                p.Name,
                hierarchyKind,
                Symbols.Full(inner),
                -1,
                false,
                "",
                false,
                false
            );
        }
        var optional =
            Symbols.Is(type, Symbols.Optional) ? ParamKind.Optional
            : Symbols.Is(type, Symbols.OptionalRef) ? ParamKind.OptionalRef
            : (ParamKind?)null;
        if (optional is { } optionalKind)
        {
            var inner = ((INamedTypeSymbol)type).TypeArguments[0];
            var innerFacts = Symbols.Facts(inner);
            if (innerFacts is null || innerFacts.IsTag)
            {
                problem = Invalid(
                    $"'{Symbols.Name(inner)}' is not a declared component with a value"
                );
                return null;
            }
            var write = optionalKind == ParamKind.OptionalRef;
            if (write && Unwritable(innerFacts) is { } why)
            {
                problem = new Problem(
                    Diagnostics.RefToUnwritableComponent,
                    null,
                    "",
                    $"'{Symbols.Name(inner)}', which is {why}"
                );
                return null;
            }
            term = new TermModel(
                Symbols.Full(inner),
                write ? TermKind.OptionalWrite : TermKind.Optional,
                -1,
                innerFacts.IsSparse
            );
            return new ParamModel(
                p.Name,
                optionalKind,
                Symbols.Full(inner),
                nextTerm,
                innerFacts.IsSparse,
                "",
                false,
                write && innerFacts.WritesOnlyStamp
            );
        }
        if (p.RefKind == RefKind.Ref && Symbols.Implements(type, Symbols.Reduction))
            return Make(ParamKind.Accumulator);

        var facts = Symbols.Facts(type);
        if (facts is null)
        {
            problem = Invalid(
                $"'{Symbols.Name(type)}' is not a declared component; mark the parameter [Arg] to pass it through the run"
            );
            return null;
        }
        if (facts.IsTag)
        {
            problem = Invalid(
                $"'{Symbols.Name(type)}' is a tag and has no value; match it with [With<T>]"
            );
            return null;
        }
        if (p.RefKind == RefKind.Ref)
        {
            if (Unwritable(facts) is { } why)
            {
                problem = new Problem(
                    Diagnostics.RefToUnwritableComponent,
                    null,
                    "",
                    $"'{Symbols.Name(type)}', which is {why}"
                );
                return null;
            }
            term = new TermModel(full, TermKind.Write, -1, facts.IsSparse);
            return Make(ParamKind.Ref, nextTerm, facts.IsSparse, stampOnly: facts.WritesOnlyStamp);
        }
        term = new TermModel(full, TermKind.Read, -1, facts.IsSparse);
        return Make(
            ParamKind.In,
            nextTerm,
            facts.IsSparse,
            p.RefKind is RefKind.In or RefKind.RefReadOnlyParameter ? "in" : ""
        );
    }

    static string? Unwritable(ComponentFacts facts) => facts.IsReplaceOnly ? "replace-only" : null;

    static void Act(
        INamedTypeSymbol host,
        string? actName,
        List<ParamModel> parameters,
        List<ActArgModel> args,
        out bool isStatic,
        Action<string> fail
    )
    {
        isStatic = false;
        if (string.IsNullOrEmpty(actName))
        {
            fail("[Collect] needs Act, the method run per collected entity");
            return;
        }
        var candidates = host.GetMembers(actName!).OfType<IMethodSymbol>().ToList();
        if (candidates.Count != 1)
        {
            fail($"Act names '{actName}', which must be exactly one method of the type");
            return;
        }
        var act = candidates[0];
        isStatic = act.IsStatic;
        if (!act.ReturnsVoid || act.TypeParameters.Length > 0)
        {
            fail($"Act '{actName}' must be a non-generic method returning void");
            return;
        }
        var hasEntity = false;
        foreach (var p in act.Parameters)
        {
            if (Symbols.Is(p.Type, Symbols.Entity) && p.RefKind == RefKind.None)
            {
                args.Add(new ActArgModel(ActArgKind.Entity, p.Name, ""));
                hasEntity = true;
            }
            else if (Symbols.Is(p.Type, Symbols.WorldEntity) && p.RefKind == RefKind.None)
            {
                args.Add(new ActArgModel(ActArgKind.WorldEntity, p.Name, ""));
                hasEntity = true;
            }
            else if (Symbols.Is(p.Type, Symbols.World) && p.RefKind == RefKind.None)
                args.Add(new ActArgModel(ActArgKind.World, p.Name, ""));
            else if (
                parameters.Find(a => a.Kind == ParamKind.Arg && a.Name == p.Name) is { } arg
                && arg.Type == Symbols.Full(p.Type)
                && p.RefKind is RefKind.None or RefKind.In
            )
                args.Add(
                    new ActArgModel(ActArgKind.Arg, p.Name, p.RefKind == RefKind.In ? "in" : "")
                );
            else
            {
                fail(
                    $"parameter '{p.Name}' of Act '{actName}' is not an Entity, WorldEntity, World or an [Arg] of the predicate"
                );
                return;
            }
        }
        if (!hasEntity)
            fail($"Act '{actName}' must take the Entity or WorldEntity it acts on");
    }
}
