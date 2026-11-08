namespace Tralys.Ecs;

/// <summary>Implemented by the generator on every declared component, tag, relation and row type.</summary>
public interface IDeclared;

/// <summary>Implemented by the generator on every declared component and tag: the types an entity holds itself. Relation and row types
/// are not components; they have their own APIs.</summary>
public interface IComponent : IDeclared;

/// <summary>A component that hands out <c>ref</c> access through edit handles.</summary>
public interface IMutableComponent : IComponent;

/// <summary>Implemented by the generator on every <see cref="ReplaceOnlyAttribute"/> component; <see cref="ObserverRegistry.OnReplace{T}"/>
/// takes one.</summary>
public interface IReplaceOnly : IComponent;

/// <summary>Implemented by the generator on every <see cref="TagAttribute"/> struct.</summary>
public interface ITag : IComponent;

/// <summary>Implemented by the generator on every <see cref="EnableableAttribute"/> component and tag; <see cref="World.SetEnabled{T}"/> takes one.</summary>
public interface IEnableable : IComponent;

/// <summary>Implemented by the generator on every <see cref="SingletonAttribute"/> component and tag; the world's singleton accessors take one.</summary>
public interface ISingleton : IComponent;

/// <summary>Implemented by the generator on <see cref="RelationAttribute"/> structs.</summary>
public interface IRelation : IDeclared;

/// <summary>Implemented by the generator on every <see cref="RowsAttribute"/> struct.</summary>
public interface IRow : IDeclared;

/// <summary>Implemented by the generator on <see cref="RowsAttribute"/> structs with a <see cref="RowKeyAttribute"/> field.</summary>
public interface IRow<TKey> : IRow
{
    /// <summary>The <see cref="RowKeyAttribute"/> field.</summary>
    TKey RowKey { get; set; }
}
