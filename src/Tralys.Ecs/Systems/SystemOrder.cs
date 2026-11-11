namespace Tralys.Ecs;

/// <summary>Declares a system set: a group of systems a schedule runs together, in the set's place in the schedule.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SystemSetAttribute : Attribute;

/// <summary>Puts the system in the <see cref="SystemSetAttribute"/> set <typeparamref name="TSet"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class InSetAttribute<TSet> : Attribute
    where TSet : class;

/// <summary>Where a system's attributes place it in a schedule; a schedule builder starts from it and may configure another set.</summary>
public sealed class SystemOrder(Type? set)
{
    /// <summary>No set.</summary>
    public static SystemOrder None { get; } = new(null);

    /// <summary>The <see cref="SystemSetAttribute"/> type the system is in, or null.</summary>
    public Type? Set { get; } = set;
}
