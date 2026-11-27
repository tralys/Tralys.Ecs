using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators.Queries;

static class QueryEmitter
{
    const string Ecs = "global::Tralys.Ecs.";

    public static string HintName(QueryModel model) =>
        $"{model.TypeHint}.{model.Method}.Query.g.cs";

    public static string Emit(QueryModel model)
    {
        var writer = new CodeWriter();
        writer.Line(ModuleEmitter.Header);
        if (model.Namespace.Length > 0)
            writer.Line($"namespace {model.Namespace};");
        writer.Line();
        var scopes = new List<CodeWriter.Scope>();
        foreach (var container in model.Containers.Items)
            scopes.Add(writer.Block($"partial {container.Keyword} {container.Name}"));

        using (writer.Block($"partial {model.TypeKeyword} {model.TypeName}"))
        {
            Desc(writer, model);
            writer.Line();
            var names = new RunNames(model);
            Run(writer, model, names);
            if (!model.IsCollect)
            {
                writer.Line();
                SingleRuns(writer, model, names);
            }
            writer.Line();
            Kernel(writer, model);
            if (model.IsCollect)
            {
                writer.Line();
                CollectActEmitter.Act(writer, model);
            }
        }

        for (var i = scopes.Count - 1; i >= 0; i--)
            scopes[i].Dispose();
        return writer.ToString();
    }

    public static string QueryField(QueryModel model) => $"__{model.Method}_Query";

    static string OwnerField(QueryModel model) => $"__{model.Method}_Owner";

    static string KernelName(QueryModel model) => $"__{model.Method}_Kernel";

    static void Desc(CodeWriter writer, QueryModel model)
    {
        writer.Line(
            $"static readonly {Ecs}QueryDesc {QueryField(model)} = {Ecs}QueryDesc.Builder()"
        );
        using (writer.Indented())
        {
            foreach (var term in model.Terms.Items)
                writer.Line(
                    term.Kind == TermKind.AnyOf
                        ? $".AnyOf<{term.Type}>({term.Group})"
                        : $".{term.Kind}<{term.Type}>()"
                );
            if (model.IncludePending)
                writer.Line(".IncludePendingDestroy()");
            if (model.IncludeDetached)
                writer.Line(".IncludeDetached()");
            writer.Line(".Build();");
        }
        if (!model.IsSystem)
        {
            writer.Line();
            writer.Line(
                $"static readonly {Ecs}SystemId {OwnerField(model)} = {Ecs}SystemId.Next();"
            );
        }
    }

    sealed class RunNames
    {
        public readonly string World;
        public readonly string Entity;
        public readonly string Entities;
        public readonly string Root;
        public readonly string Parent;
        public readonly string Source;

        public RunNames(QueryModel model)
        {
            var taken = new HashSet<string>(
                model.Params.Items.Where(p => p.Kind == ParamKind.Arg).Select(p => p.Name)
            );
            string Name(string name) => taken.Contains(name) ? "__" + name : name;
            World = Name("world");
            Entity = Name("entity");
            Entities = Name("entities");
            Root = Name("root");
            Parent = Name("parent");
            Source = Name("source");
        }
    }

    static IEnumerable<ParamModel> Args(QueryModel model) =>
        model.Params.Items.Where(p => p.Kind == ParamKind.Arg);

    static string ArgList(QueryModel model) =>
        string.Concat(
            Args(model)
                .Select(a =>
                    ", " + (a.Modifier.Length > 0 ? a.Modifier + " " : "") + $"{a.Type} {a.Name}"
                )
        );

    static string ReturnType(QueryModel model) => model.Accumulator ?? "void";

    static void Run(CodeWriter writer, QueryModel model, RunNames names)
    {
        var modifiers = model.Accessibility + (model.RunStatic ? " static" : "");
        using (
            writer.Block(
                $"{modifiers} {ReturnType(model)} Run{model.Method}({Ecs}World {names.World}{ArgList(model)})"
            )
        )
        {
            Prologue(writer, model, names);
            var query = $"{names.World}.Query({QueryField(model)})";
            if (model.IsCollect)
            {
                CollectActEmitter.CreateAct(writer, model, names.World);
                writer.Line($"{query}.Collect(ref __kernel, ref __act);");
            }
            else if (model.Accumulator is { } accumulator)
                writer.Line(
                    $"return {query}.{(model.Parallel ? "ReduceParallel" : "Reduce")}<{accumulator}, {KernelName(model)}>(ref __kernel);"
                );
            else
                writer.Line(
                    $"{query}.{(model.Parallel ? "RunParallel" : model.DepthOrdered ? "RunDepthOrdered" : "Run")}(ref __kernel);"
                );
        }
    }

    static void SingleRuns(CodeWriter writer, QueryModel model, RunNames names)
    {
        var modifiers = model.Accessibility + (model.RunStatic ? " static" : "");
        var forward = string.Concat(
            Args(model).Select(a => ", " + (a.Modifier.Length > 0 ? a.Modifier + " " : "") + a.Name)
        );
        writer.Line(
            $"{modifiers} {ReturnType(model)} Run{model.Method}For({Ecs}World {names.World}, {Ecs}Entity {names.Entity}{ArgList(model)}) =>"
        );
        writer.Line(
            $"    Run{model.Method}For({names.World}, new global::System.ReadOnlySpan<{Ecs}Entity>(in {names.Entity}){forward});"
        );
        writer.Line();
        using (
            writer.Block(
                $"{modifiers} {ReturnType(model)} Run{model.Method}For({Ecs}World {names.World}, global::System.ReadOnlySpan<{Ecs}Entity> {names.Entities}{ArgList(model)})"
            )
        )
        {
            Prologue(writer, model, names);
            RunFor(writer, model, names, names.Entities);
        }
        Scoped(
            writer,
            model,
            names,
            $"Run{model.Method}ForSubtree",
            $"{Ecs}Entity {names.Root}",
            $"SnapshotSubtree({names.Root})",
            ""
        );
        Scoped(
            writer,
            model,
            names,
            $"Run{model.Method}ChildrenOf",
            $"{Ecs}Entity {names.Parent}",
            $"SnapshotChildren({names.Parent})",
            ""
        );
        if (model.DepthOrdered)
            return;
        Scoped(
            writer,
            model,
            names,
            $"Run{model.Method}TargetsOf<TRelation>",
            $"{Ecs}Entity {names.Source}",
            $"SnapshotTargets<TRelation>({names.Source})",
            $"where TRelation : struct, {Ecs}IRelation"
        );
    }

    static void Scoped(
        CodeWriter writer,
        QueryModel model,
        RunNames names,
        string method,
        string parameter,
        string snapshot,
        string constraint
    )
    {
        var modifiers = model.Accessibility + (model.RunStatic ? " static" : "");
        writer.Line();
        writer.Line(
            $"{modifiers} {ReturnType(model)} {method}({Ecs}World {names.World}, {parameter}{ArgList(model)})"
        );
        if (constraint.Length > 0)
            writer.Line("    " + constraint);
        using (writer.Open())
        {
            Prologue(writer, model, names);
            writer.Line($"using var __scope = {names.World}.{snapshot};");
            RunFor(writer, model, names, "__scope.Span");
        }
    }

    static void RunFor(CodeWriter writer, QueryModel model, RunNames names, string entities)
    {
        var query = $"{names.World}.Query({QueryField(model)})";
        writer.Line(
            model.Accumulator is { } accumulator
                ? $"return {query}.ReduceFor<{accumulator}, {KernelName(model)}>(ref __kernel, {entities});"
                : $"{query}.RunFor(ref __kernel, {entities});"
        );
    }

    static void Prologue(CodeWriter writer, QueryModel model, RunNames names)
    {
        var run = model.IsSystem ? "Descriptor.Id" : OwnerField(model);
        writer.Line(
            $"using var __run = global::Tralys.Ecs.Generated.Plumbing.EnsureSystemRun({names.World}, {run});"
        );
        if (model.IsSystem && !model.RunStatic)
            writer.Line($"BindLookups({names.World});");
        foreach (var lookup in model.Lookups.Items)
        {
            if (lookup.IsStatic || !model.RunStatic)
                writer.Line($"{lookup.Name} = new({names.World});");
        }
        var fields = new List<string>();
        if (!model.IsStatic)
            fields.Add("__self = this");
        if (TakesWorld(model))
            fields.Add($"__world = {names.World}");
        fields.AddRange(Args(model).Select(a => $"__arg_{a.Name} = {a.Name}"));
        writer.Line(
            fields.Count == 0
                ? $"var __kernel = new {KernelName(model)}();"
                : $"var __kernel = new {KernelName(model)} {{ {string.Join(", ", fields)} }};"
        );
    }

    static bool TakesWorld(QueryModel model) =>
        model.Params.Items.Any(p => p.Kind == ParamKind.World);

    static void Kernel(CodeWriter writer, QueryModel model)
    {
        var kind = model.Parallel ? "struct" : "ref struct";
        var contract = model.Accumulator is { } accumulator
            ? $"{Ecs}IReducingKernel<{accumulator}>"
            : $"{Ecs}IChunkKernel";
        using (writer.Block($"{kind} {KernelName(model)} : {contract}"))
        {
            if (!model.IsStatic)
                writer.Line($"public {model.TypeName} __self;");
            if (TakesWorld(model))
                writer.Line($"public {Ecs}World __world;");
            foreach (var arg in Args(model))
                writer.Line($"public {arg.Type} __arg_{arg.Name};");
            if (!model.IsStatic || TakesWorld(model) || Args(model).Any())
                writer.Line();
            var accumulatorParam = model.Accumulator is { } acc ? $", ref {acc} __acc" : "";
            using (
                writer.Block($"public void Execute(ref {Ecs}ChunkView __chunk{accumulatorParam})")
            )
                Body(writer, model);
        }
    }

    static void Body(CodeWriter writer, QueryModel model)
    {
        var parameters = model.Params.Items;
        if (
            parameters.Any(p =>
                p.Kind is ParamKind.Entity or ParamKind.Parent or ParamKind.Up
                || (p.Sparse && p.Term >= 0)
            )
        )
            writer.Line("var __entities = __chunk.Entities;");
        var chunkLocals = new HashSet<ParamKind>();
        for (var n = 0; n < parameters.Length; n++)
        {
            var p = parameters[n];
            switch (p.Kind)
            {
                case ParamKind.In or ParamKind.Optional when !p.Sparse:
                    writer.Line(
                        $"var __c{n} = {Ecs}Generated.Plumbing.Column<{p.Type}>(in __chunk, {p.Term});"
                    );
                    break;
                case ParamKind.Ref or ParamKind.OptionalRef when !p.Sparse:
                    writer.Line(
                        $"var __t{n} = {Ecs}Generated.Plumbing.{(p.StampOnly ? "StampOnly" : "Tracked")}<{p.Type}>(in __chunk, {p.Term});"
                    );
                    break;
                case ParamKind.In or ParamKind.Optional or ParamKind.Ref or ParamKind.OptionalRef:
                    writer.Line(
                        $"var __s{n} = {Ecs}Generated.Plumbing.Sparse<{p.Type}>(in __chunk, {p.Term});"
                    );
                    break;
                case ParamKind.Commands when chunkLocals.Add(p.Kind):
                    writer.Line("var __commands = __chunk.Commands;");
                    break;
                case ParamKind.Context when chunkLocals.Add(p.Kind):
                    writer.Line("var __context = __chunk.Context;");
                    break;
                case ParamKind.Reader when chunkLocals.Add(p.Kind):
                    writer.Line("var __reader = __chunk.Reader;");
                    break;
                case ParamKind.Up:
                    writer.Line($"var __u{n} = new {Ecs}Generated.UpCache<{p.Type}>(in __chunk);");
                    break;
            }
        }

        var reduces = model.Accumulator is not null;
        if (reduces)
            writer.Line("var __a = __acc;");
        using (
            writer.Block(
                $"for (var __bits = {Ecs}Generated.Plumbing.RowMask(in __chunk); __bits != 0; __bits &= __bits - 1)"
            )
        )
        {
            writer.Line(
                "var __i = global::System.Numerics.BitOperations.TrailingZeroCount(__bits);"
            );
            writer.Line($"if (!{Ecs}Generated.Plumbing.Visits(in __chunk, __i)) continue;");
            if (parameters.Any(IsValue))
                writer.Line($"var __p = {Ecs}Generated.Plumbing.Pending(in __chunk, __i);");
            var guards = new List<string>();
            var before = new List<string>();
            var after = new List<string>();
            var args = new List<string>();
            for (var n = 0; n < parameters.Length; n++)
                Row(parameters[n], n, guards, before, after, args);
            foreach (var line in guards.Concat(before))
                writer.Line(line);

            var target = model.IsStatic ? model.TypeName : "__self";
            var call = $"{target}.{model.Method}({string.Join(", ", args)})";
            if (model.IsCollect)
            {
                writer.Line($"var __hit = {call};");
                foreach (var line in after)
                    writer.Line(line);
                writer.Line("if (__hit)");
                writer.Line($"    {Ecs}Generated.Plumbing.Collect(in __chunk, __i);");
            }
            else if (model.ReturnsBool)
            {
                writer.Line($"var __go = {call};");
                foreach (var line in after)
                    writer.Line(line);
                using (writer.Block("if (!__go)"))
                {
                    if (reduces)
                        writer.Line("__acc = __a;");
                    writer.Line("__chunk.Stop();");
                    writer.Line("return;");
                }
            }
            else
            {
                writer.Line(call + ";");
                foreach (var line in after)
                    writer.Line(line);
            }
        }
        if (reduces)
            writer.Line("__acc = __a;");
    }

    static void Row(
        ParamModel p,
        int n,
        List<string> guards,
        List<string> before,
        List<string> after,
        List<string> args
    )
    {
        var value = p.Modifier.Length > 0 ? p.Modifier + " " : "";
        switch (p.Kind)
        {
            case ParamKind.Entity:
                args.Add("__entities[__i]");
                return;
            case ParamKind.Commands:
                args.Add("__commands");
                return;
            case ParamKind.Context:
                args.Add("__context");
                return;
            case ParamKind.World:
                args.Add("__world");
                return;
            case ParamKind.Reader:
                args.Add("__reader");
                return;
            case ParamKind.Arg:
                args.Add($"{value}__arg_{p.Name}");
                return;
            case ParamKind.Accumulator:
                args.Add("ref __a");
                return;
            case ParamKind.Parent:
                args.Add(
                    $"{Ecs}Generated.Plumbing.ParentTerm<{p.Type}>(in __chunk, __entities[__i])"
                );
                return;
            case ParamKind.Up:
                args.Add($"__u{n}.Find(__entities[__i])");
                return;
        }

        const string P = Ecs + "Generated.Plumbing";
        var overlay = $"var __e{n} = __p ? {P}.Overlay(in __chunk, {p.Term}, __i) : -1;";
        string Overlaid(string outs) =>
            $"{P}.Overlaid<{p.Type}>(in __chunk, {p.Term}, __i, {outs})";
        var find =
            $"ref var __v{n} = ref __e{n} > 0 ? ref {Overlaid($"out __o{n}, out __slot{n}")} : ref {P}.Find(in __s{n}, __entities[__i], out __slot{n});";
        var stored = $"__e{n} > 0 || (__e{n} < 0 && __slot{n} >= 0)";
        if (p.Sparse)
        {
            var into = p.Kind is ParamKind.In or ParamKind.Ref ? guards : before;
            into.Add(overlay);
            into.Add($"var __o{n} = false;");
            into.Add($"var __slot{n} = -1;");
            into.Add(find);
            into.Add($"var __h{n} = {stored};");
        }
        switch (p.Kind)
        {
            case ParamKind.In when p.Sparse:
                guards.Add($"if (!__h{n}) continue;");
                args.Add($"{value}__v{n}");
                return;
            case ParamKind.In:
                before.Add(overlay);
                before.Add(
                    $"ref var __v{n} = ref __e{n} > 0 ? ref {Overlaid("out _, out _")} : ref __c{n}[__i];"
                );
                args.Add($"{value}__v{n}");
                return;
            case ParamKind.Ref when p.Sparse:
                guards.Add($"if (!__h{n}) continue;");
                before.Add($"var __b{n} = !__o{n} && {P}.Compares(in __s{n}) ? __v{n} : default;");
                args.Add($"ref __v{n}");
                after.Add(
                    $"if (!__o{n} && {P}.Compares(in __s{n})) {P}.Release(in __s{n}, __slot{n}, in __b{n});"
                );
                return;
            case ParamKind.Ref:
            {
                var (gate, release) = Release(p, n);
                before.Add(overlay);
                before.Add($"var __o{n} = false;");
                before.Add(
                    $"ref var __v{n} = ref __e{n} > 0 ? ref {Overlaid($"out __o{n}, out _")} : ref {P}.Unstamped(in __t{n}, __i);"
                );
                before.Add($"var __b{n} = !__o{n} && {Gate(gate, n)} ? __v{n} : default;");
                args.Add($"ref __v{n}");
                after.Add($"if (!__o{n} && {Gate(gate, n)}{release}");
                return;
            }
            case ParamKind.Optional when p.Sparse:
                args.Add($"__h{n} ? {P}.Optional<{p.Type}>(in __v{n}) : default");
                return;
            case ParamKind.Optional:
                before.Add(overlay);
                args.Add(
                    $"__e{n} > 0 ? {P}.Optional<{p.Type}>(in {Overlaid("out _, out _")}) : __e{n} < 0 && {P}.HasRow(in __chunk, {p.Term}, __i) ? {P}.Optional<{p.Type}>(in __c{n}[__i]) : default"
                );
                return;
            case ParamKind.OptionalRef when p.Sparse:
                before.Add(
                    $"var __b{n} = __h{n} && !__o{n} && {P}.Compares(in __s{n}) ? __v{n} : default;"
                );
                args.Add($"__h{n} ? {P}.OptionalRef<{p.Type}>(ref __v{n}) : default");
                after.Add(
                    $"if (__h{n} && !__o{n} && {P}.Compares(in __s{n})) {P}.Release(in __s{n}, __slot{n}, in __b{n});"
                );
                return;
            case ParamKind.OptionalRef:
            {
                var (gate, release) = Release(p, n);
                before.Add(overlay);
                before.Add($"var __o{n} = false;");
                before.Add(
                    $"var __h{n} = __e{n} > 0 || (__e{n} < 0 && {P}.HasRow(in __chunk, {p.Term}, __i));"
                );
                before.Add(
                    $"ref var __v{n} = ref __e{n} > 0 ? ref {Overlaid($"out __o{n}, out _")} : ref (__h{n} ? ref {P}.Unstamped(in __t{n}, __i) : ref global::System.Runtime.CompilerServices.Unsafe.NullRef<{p.Type}>());"
                );
                before.Add(
                    $"var __b{n} = __h{n} && !__o{n} && {Gate(gate, n)} ? __v{n} : default;"
                );
                args.Add($"__h{n} ? {P}.OptionalRef<{p.Type}>(ref __v{n}) : default");
                after.Add($"if (__h{n} && !__o{n} && {Gate(gate, n)}{release}");
                return;
            }
        }
    }

    static bool IsValue(ParamModel p) =>
        p.Kind is ParamKind.In or ParamKind.Ref or ParamKind.Optional or ParamKind.OptionalRef;

    static string Gate(string gate, int n) =>
        gate == "Tracks" ? $"__t{n}.Tracks" : $"{Ecs}Generated.Plumbing.Compares(in __t{n})";

    // Indexes stay current under Untracked() too, so their release gates on Compares, not Tracks.
    // The compare stays in the row loop so an unchanged row makes no call; the call does the index, buffer and stamp work.
    static (string Gate, string Release) Release(ParamModel p, int n) =>
        p.StampOnly
            ? ("Tracks", $") {Ecs}Generated.Plumbing.StampIfChanged(in __t{n}, __i, in __b{n});")
            : (
                "Compares",
                $" && !{Ecs}Generated.Plumbing.Same(in __b{n}, in __v{n})) {Ecs}Generated.Plumbing.Changed(in __t{n}, __i, in __b{n});"
            );
}
