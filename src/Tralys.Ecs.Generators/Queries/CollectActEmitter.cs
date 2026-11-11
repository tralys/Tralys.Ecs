using Tralys.Ecs.Generators.CodeGen;

namespace Tralys.Ecs.Generators.Queries;

static class CollectActEmitter
{
    const string Ecs = "global::Tralys.Ecs.";

    static string ActName(QueryModel model) => $"__{model.Method}_Act";

    public static void CreateAct(CodeWriter writer, QueryModel model, string world)
    {
        var fields = new List<string>();
        if (!model.ActStatic)
            fields.Add("__self = this");
        fields.Add($"__world = {world}");
        fields.AddRange(
            model
                .ActArgs.Items.Where(a => a.Kind == ActArgKind.Arg)
                .Select(a => $"__arg_{a.Name} = {a.Name}")
        );
        writer.Line($"var __act = new {ActName(model)} {{ {string.Join(", ", fields)} }};");
    }

    public static void Act(CodeWriter writer, QueryModel model)
    {
        using (writer.Block($"ref struct {ActName(model)} : {Ecs}IEntityAction"))
        {
            if (!model.ActStatic)
                writer.Line($"public {model.TypeName} __self;");
            writer.Line($"public {Ecs}World __world;");
            foreach (var arg in model.ActArgs.Items.Where(a => a.Kind == ActArgKind.Arg))
            {
                var type = model
                    .Params.Items.First(p => p.Kind == ParamKind.Arg && p.Name == arg.Name)
                    .Type;
                writer.Line($"public {type} __arg_{arg.Name};");
            }
            writer.Line();
            var args = model.ActArgs.Items.Select(a =>
                a.Kind switch
                {
                    ActArgKind.Entity => "__entity",
                    ActArgKind.WorldEntity => $"new {Ecs}WorldEntity(__world, __entity)",
                    ActArgKind.World => "__world",
                    _ => (a.Modifier.Length > 0 ? a.Modifier + " " : "") + $"__arg_{a.Name}",
                }
            );
            var target = model.ActStatic ? model.TypeName : "__self";
            writer.Line(
                $"public void Act({Ecs}Entity __entity) => {target}.{model.Act}({string.Join(", ", args)});"
            );
        }
    }
}
