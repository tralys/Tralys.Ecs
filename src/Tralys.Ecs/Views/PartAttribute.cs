namespace Tralys.Ecs;

/// <summary>A component or tag of a <see cref="ViewAttribute"/> struct. A required component part hands out <c>ref readonly</c> access, a
/// <c>Replace</c> method that throws if the entity no longer has the part, and, for a mutable component, an edit handle. A required tag
/// part only narrows which entities the view matches.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class PartAttribute<T> : Attribute
    where T : struct, IComponent
{
    /// <summary>The view only reads the part: no write methods and no edit handle.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>The view matches entities without the part and exposes it as a nullable value. A component part gets
    /// <c>SetOrRemove</c>; a tag part gets <c>Add</c> and <c>Remove</c>.</summary>
    public bool Optional { get; set; }
}
