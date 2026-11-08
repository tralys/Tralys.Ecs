namespace Tralys.Ecs.Generated;

/// <summary>Emitted by the generator: the type names an assembly's module declares, read by the duplicate-declaration analyzer
/// across references.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ComponentModuleManifestAttribute(string[] names) : Attribute
{
    /// <summary>The fully qualified name of each declared type, closed generic instances included.</summary>
    public string[] Names { get; } = names;
}
