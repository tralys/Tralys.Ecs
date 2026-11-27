using System.Text;
using Microsoft.CodeAnalysis;

namespace Tralys.Ecs.Generators.Metadata;

// What a stored value's bytes mean through nested structs and buffer elements, never a type or field name, so only a layout change refuses them.
static class LayoutKey
{
    const int MaxDepth = 16;

    public static ulong Of(ITypeSymbol type)
    {
        var text = new StringBuilder();
        Append(text, type, 0);
        var hash = 14695981039346656037UL;
        foreach (var c in text.ToString())
            hash = (hash ^ c) * 1099511628211UL;
        return hash;
    }

    static void Append(StringBuilder text, ITypeSymbol type, int depth)
    {
        if (type.SpecialType != SpecialType.None)
        {
            text.Append(type.SpecialType);
            return;
        }
        if (type is IPointerTypeSymbol pointer)
        {
            text.Append('*');
            Append(text, pointer.PointedAtType, depth + 1);
            return;
        }
        if (!type.IsValueType)
        {
            text.Append(type.TypeKind);
            return;
        }
        if (Symbols.Is(type, Symbols.Entity))
        {
            text.Append("Entity");
            return;
        }
        if (depth > MaxDepth)
            return;
        if (
            type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }
        )
        {
            text.Append("enum:").Append(underlying.SpecialType);
            return;
        }
        if (
            type is INamedTypeSymbol { TypeArguments.Length: > 0 } named
            && Symbols.Is(type, Symbols.BufferHandle)
        )
        {
            text.Append("buffer<");
            Append(text, named.TypeArguments[0], depth + 1);
            text.Append('>');
            return;
        }
        foreach (var attribute in type.GetAttributes())
        {
            switch (attribute.AttributeClass?.ToDisplayString())
            {
                case "System.Runtime.CompilerServices.InlineArrayAttribute":
                    text.Append("[inline:")
                        .Append(attribute.ConstructorArguments.FirstOrDefault().Value)
                        .Append(']');
                    break;
                case "System.Runtime.InteropServices.StructLayoutAttribute":
                    AppendLayout(text, attribute);
                    break;
            }
        }
        text.Append('{');
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            if (field.IsFixedSizeBuffer)
                text.Append("fixed").Append(field.FixedSize).Append(':');
            foreach (var attribute in field.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                if (
                    name
                    is "Tralys.Ecs.CaptureAttribute"
                        or "Tralys.Ecs.RowKeyAttribute"
                        or "System.Runtime.InteropServices.FieldOffsetAttribute"
                )
                    text.Append('[').Append(attribute).Append(']');
            }
            Append(text, field.Type, depth + 1);
            text.Append(';');
        }
        text.Append('}');
    }

    // Sequential with no Pack or Size is what a struct gets without the attribute, so spelling it out keeps the key.
    static void AppendLayout(StringBuilder text, AttributeData layout)
    {
        var kind = Convert.ToInt32(layout.ConstructorArguments.FirstOrDefault().Value ?? 0);
        var pack = Symbols.Named(layout, "Pack", 0);
        var size = Symbols.Named(layout, "Size", 0);
        if (kind == 0 && pack == 0 && size == 0)
            return;
        text.Append("[layout:")
            .Append(kind)
            .Append(',')
            .Append(pack)
            .Append(',')
            .Append(size)
            .Append(']');
    }
}
