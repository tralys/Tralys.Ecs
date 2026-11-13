using Tralys.Ecs.Generators.CodeGen;
using Tralys.Ecs.Generators.Metadata;

namespace Tralys.Ecs.Generators.Indexes;

static class IndexEmitter
{
    static readonly string[] Views = ["UniqueIndex", "MultiIndex", "OrderedIndex"];
    static readonly string[] Factories = ["Unique", "Multi", "Ordered"];

    public static void Keyer(CodeWriter writer, TypeModel model, IndexModel index)
    {
        writer.Line(ModuleEmitter.Hidden);
        using (
            writer.Block(
                $"public readonly struct __EcsIndex{index.Ordinal} : global::Tralys.Ecs.Generated.IIndexKey<{model.FullName}, {index.KeyType}>"
            )
        )
            writer.Line(
                $"public static bool TryOf(in {model.FullName} v, global::Tralys.Ecs.Entity parent, out {index.KeyType} key) "
                    + (
                        index.TryCall is null
                            ? $"{{ key = {index.KeyExpression}; return true; }}"
                            : $"{{ var found = {index.TryCall}; key = {index.KeyExpression}; return found; }}"
                    )
            );
    }

    public static void Accessors(CodeWriter writer, TypeModel model)
    {
        var owner =
            string.Concat(model.Containers.Items.Select(c => c.Name)) + model.DeclarationName;
        using (writer.Block($"{model.AccessorVisibility} static class {owner}Indexes"))
        {
            foreach (var index in model.Indexes.Items)
            {
                var view = $"global::Tralys.Ecs.{Views[index.Kind]}<{index.KeyType}>";
                var call =
                    $"global::Tralys.Ecs.Generated.Plumbing.{Views[index.Kind]}Of<{model.FullName}, {index.KeyType}>";
                writer.Line(
                    $"public static {view} {model.DeclarationName}{index.Name}(this global::Tralys.Ecs.World world) => {call}(world, {index.Ordinal});"
                );
                writer.Line(
                    $"public static {view} {index.Name}(this global::Tralys.Ecs.IndexLookup<{model.FullName}> lookup) => "
                        + $"{call}(global::Tralys.Ecs.Generated.Plumbing.WorldOf(lookup), {index.Ordinal});"
                );
            }
        }
    }

    public static string Declaration(IndexModel index, string owner) =>
        $"new global::Tralys.Ecs.IndexDeclaration((global::Tralys.Ecs.IndexKind){index.Kind}, \"{index.Name}\", "
        + $"[{string.Join(", ", index.Fields.Items.Select(f => "\"" + f + "\""))}], {ModuleEmitter.Quote(index.KeyFrom)}, "
        + $"(global::Tralys.Ecs.ConflictPolicy){index.OnConflict}, {(index.PerParent ? "true" : "false")}, "
        + $"global::Tralys.Ecs.Generated.IndexFactory.{Factories[index.Kind]}<{owner}, {index.KeyType}, {owner}.__EcsIndex{index.Ordinal}>())";
}
