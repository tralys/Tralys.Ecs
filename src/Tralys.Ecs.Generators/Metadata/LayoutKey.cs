using System.Text;
using Microsoft.CodeAnalysis;

namespace Tralys.Ecs.Generators.Metadata;

// What a stored value's bytes mean, down through nested structs and buffer elements, so bytes from another layout are refused.
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
        text.Append(Symbols.Full(type));
        if (depth > MaxDepth || !type.IsValueType || type.SpecialType != SpecialType.None)
            return;
        if (
            type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }
        )
        {
            text.Append(':').Append(Symbols.Full(underlying));
            return;
        }
        if (
            type is INamedTypeSymbol { TypeArguments.Length: > 0 } named
            && Symbols.Is(type, Symbols.BufferHandle)
        )
        {
            text.Append('<');
            Append(text, named.TypeArguments[0], depth + 1);
            text.Append('>');
            return;
        }
        foreach (var attribute in type.GetAttributes())
        {
            if (
                attribute.AttributeClass?.ToDisplayString()
                == "System.Runtime.CompilerServices.InlineArrayAttribute"
            )
                text.Append('[')
                    .Append(attribute.ConstructorArguments.FirstOrDefault().Value)
                    .Append(']');
        }
        text.Append('{');
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            text.Append(field.AssociatedSymbol?.Name ?? field.Name).Append(':');
            if (field.IsFixedSizeBuffer)
                text.Append("fixed").Append(field.FixedSize).Append(':');
            foreach (var attribute in field.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                if (name is "Tralys.Ecs.CaptureAttribute" or "Tralys.Ecs.RowKeyAttribute")
                    text.Append('[').Append(attribute).Append(']');
            }
            Append(text, field.Type, depth + 1);
            text.Append(';');
        }
        text.Append('}');
    }
}
