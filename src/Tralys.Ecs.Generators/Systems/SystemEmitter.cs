using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators.Systems;

static class SystemEmitter
{
    const string Ecs = "global::Tralys.Ecs.";

    public static string Emit(SystemModel model)
    {
        var writer = new CodeWriter();
        writer.Line(ModuleEmitter.Header);
        if (model.Namespace.Length > 0)
            writer.Line($"namespace {model.Namespace};");
        writer.Line();
        var scopes = new List<CodeWriter.Scope>();
        foreach (var container in model.Containers.Items)
            scopes.Add(writer.Block($"partial {container.Keyword} {container.Name}"));

        using (writer.Block($"partial {model.Keyword} {model.TypeName}"))
        {
            writer.Line($"public static {Ecs}SystemDescriptor Descriptor => __EcsSystem.Value;");
            writer.Line();
            writer.Line(
                $"protected override {Ecs}SystemDescriptor CreateDescriptor() => __EcsSystem.Value;"
            );
            writer.Line();
            using (writer.Block($"protected override void BindLookups({Ecs}World world)"))
            {
                writer.Line("base.BindLookups(world);");
                foreach (var field in model.Lookups.Items)
                    writer.Line($"{field} = new(world);");
            }
            writer.Line();
            using (writer.Block("static class __EcsSystem"))
            {
                writer.Line($"internal static readonly {Ecs}SystemDescriptor Value = new(");
                using (writer.Indented())
                {
                    writer.Line($"\"{model.DisplayName}\",");
                    writer.Line($"typeof({model.FullName}),");
                    writer.Line(
                        $"new {Ecs}SystemOrder({(model.Set is null ? "null" : $"typeof({model.Set})")}),"
                    );
                    writer.Line(model.Parallel ? "true" : "false");
                }
                writer.Line(");");
            }
        }

        for (var i = scopes.Count - 1; i >= 0; i--)
            scopes[i].Dispose();
        return writer.ToString();
    }
}
