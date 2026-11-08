namespace Tralys.Ecs.Generated;

/// <summary>Marks a type whose instances must be disposed; the analyzer reports one that is neither disposed nor handed on, naming
/// <see cref="Consequence"/>.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class MustDisposeAttribute(string consequence) : Attribute
{
    /// <summary>What is lost when an instance is not disposed, phrased to follow "or".</summary>
    public string Consequence { get; } = consequence;
}
