using System.Linq;
using System.Reflection;

namespace Stellar.Host;

/// <summary>
/// Locates a VContainer-shaped <c>IObjectResolver</c> hanging off an arbitrary game object
/// (typically <c>Panda.Core.Game.GameRoot</c>). Falls back to scanning all fields/properties
/// for anything whose type implements an <c>IObjectResolver</c> interface or has
/// <c>VContainer</c> in its full name.
/// </summary>
internal static class ResolverProbe
{
    private static readonly string[] PreferredNames = { "Container", "Resolver", "ObjectResolver" };

    public static object? FindOn(object? root)
    {
        if (root is null)
        {
            return null;
        }

        var type = root.GetType();

        foreach (var name in PreferredNames)
        {
            if (TryRead(root, type.GetProperty(name)) is { } fromProperty)
            {
                return fromProperty;
            }
            if (TryRead(root, type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) is { } fromField)
            {
                return fromField;
            }
        }

        foreach (var property in type.GetProperties())
        {
            if (LooksLikeResolver(property.PropertyType) && TryRead(root, property) is { } value)
            {
                return value;
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (LooksLikeResolver(field.FieldType) && TryRead(root, field) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static object? TryRead(object root, PropertyInfo? property)
    {
        if (property is null)
        {
            return null;
        }
        try
        {
            return property.GetValue(root);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryRead(object root, FieldInfo? field)
    {
        if (field is null)
        {
            return null;
        }
        try
        {
            return field.GetValue(root);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeResolver(System.Type type)
    {
        if (type.FullName?.Contains("VContainer") == true)
        {
            return true;
        }
        return type.GetInterfaces().Any(i => i.Name == "IObjectResolver");
    }
}
