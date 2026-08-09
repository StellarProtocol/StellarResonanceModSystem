using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Static IL2CPP-interop reflection floor shared by every plugin. Pure BCL reflection over the live
/// <see cref="AppDomain"/> — no Unity / game / HarmonyX references — so it lives in the contract layer
/// alongside <c>VirtualListMath</c> and can be called from static Harmony patch classes that hold no
/// <see cref="IPluginServices"/> handle.
/// <para><b>IL2CPP notes:</b> il2cpp fields surface as <see cref="PropertyInfo"/> (never
/// <see cref="FieldInfo"/>); il2cpp lists (<c>ZList&lt;T&gt;</c>) are not <see cref="System.Collections.Generic.IEnumerable{T}"/>, so walk
/// them with <see cref="Count"/> + <see cref="Item"/>; <c>ZSingleton&lt;T&gt;.Instance</c> is an inherited
/// static member reachable via the base-chain walk in <see cref="GetSingleton(Type)"/>.</para>
/// </summary>
public static class StellarInterop
{
    // Only SUCCESSFUL (non-null) resolutions are cached. A miss is never cached because game types load
    // lazily during boot/login — caching a null would permanently pin a type as "not found".
    private static readonly ConcurrentDictionary<string, Type> _typeCache = new(StringComparer.Ordinal);

    private const BindingFlags AnyDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Resolves a type by full name across every loaded assembly. Successful results are cached;
    /// misses are not (game types load lazily, so a later call can still succeed). Returns null if absent.</summary>
    /// <param name="fullName">Assembly-qualified-optional full type name (e.g. <c>"LuaInterface.LuaState"</c>).</param>
    public static Type? FindType(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        if (_typeCache.TryGetValue(fullName, out var cached)) return cached;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t;
            try { t = asm.GetType(fullName); }
            catch { t = null; }
            if (t is not null) { _typeCache[fullName] = t; return t; }
        }
        return null;
    }

    /// <summary>Reads the static singleton accessor (<c>Instance</c> property or field) off <paramref name="t"/>,
    /// walking the base chain so an inherited <c>ZSingleton&lt;T&gt;.Instance</c> resolves. Returns null if absent
    /// or if the getter throws.</summary>
    /// <param name="t">The singleton type.</param>
    public static object? GetSingleton(Type? t)
    {
        if (t is null) return null;
        var prop = FindPropertyUp(t, "Instance");
        if (prop?.GetGetMethod(nonPublic: true) is { IsStatic: true } getter)
        {
            try { return getter.Invoke(null, null); }
            catch { return null; }
        }
        var field = FindFieldUp(t, "Instance");
        if (field is { IsStatic: true })
        {
            try { return field.GetValue(null); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>Resolves <paramref name="typeFullName"/> via <see cref="FindType"/> then returns its singleton
    /// instance (see <see cref="GetSingleton(Type)"/>). Returns null if the type is not loaded yet.</summary>
    /// <param name="typeFullName">Full type name of the singleton.</param>
    public static object? GetSingleton(string typeFullName) => GetSingleton(FindType(typeFullName));

    /// <summary>Finds the first non-generic method named <paramref name="name"/> with exactly
    /// <paramref name="paramCount"/> parameters, walking the base chain. Use when an exact-signature
    /// <c>GetMethod</c> fails because an il2cpp proxy uses <c>Il2CppSystem</c> parameter types.</summary>
    /// <param name="t">Declaring (or derived) type to search.</param>
    /// <param name="name">Method name.</param>
    /// <param name="paramCount">Exact parameter count to match.</param>
    public static MethodInfo? FindMethod(Type? t, string name, int paramCount)
    {
        for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var m in cur.GetMethods(AnyDeclared))
            {
                if (m.Name != name || m.IsGenericMethodDefinition) continue;
                if (m.GetParameters().Length == paramCount) return m;
            }
        }
        return null;
    }

    /// <summary>Finds a property named <paramref name="name"/> by walking the base chain (il2cpp fields surface as
    /// properties, and inherited members are not returned without a base-chain walk). Returns null if absent.</summary>
    /// <param name="t">Starting type.</param>
    /// <param name="name">Property name.</param>
    public static PropertyInfo? FindPropertyUp(Type? t, string name)
    {
        for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            var p = cur.GetProperty(name, AnyDeclared);
            if (p is not null) return p;
        }
        return null;
    }

    /// <summary>Finds a field named <paramref name="name"/> by walking the base chain. Rarely needed on il2cpp
    /// (fields are properties there) but present for the occasional managed backing field. Returns null if absent.</summary>
    /// <param name="t">Starting type.</param>
    /// <param name="name">Field name.</param>
    public static FieldInfo? FindFieldUp(Type? t, string name)
    {
        for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            var f = cur.GetField(name, AnyDeclared);
            if (f is not null) return f;
        }
        return null;
    }

    /// <summary>Reads the <c>Count</c> property off an il2cpp list/collection. Returns 0 when the object is null
    /// or exposes no readable <c>Count</c>.</summary>
    /// <param name="il2cppList">The il2cpp list instance.</param>
    public static int Count(object? il2cppList)
    {
        if (il2cppList is null) return 0;
        var getter = FindPropertyUp(il2cppList.GetType(), "Count")?.GetGetMethod(nonPublic: true);
        if (getter is null) return 0;
        try { return Convert.ToInt32(getter.Invoke(il2cppList, null)); }
        catch { return 0; }
    }

    /// <summary>Reads element <paramref name="index"/> off an il2cpp list via its indexer (<c>Item</c> property or
    /// <c>get_Item(int)</c> method). Returns null on any failure.</summary>
    /// <param name="il2cppList">The il2cpp list instance.</param>
    /// <param name="index">Zero-based element index.</param>
    public static object? Item(object? il2cppList, int index)
    {
        if (il2cppList is null) return null;
        var t = il2cppList.GetType();
        var indexer = FindPropertyUp(t, "Item")?.GetGetMethod(nonPublic: true);
        if (indexer is not null)
        {
            try { return indexer.Invoke(il2cppList, new object[] { index }); }
            catch { return null; }
        }
        var m = FindMethod(t, "get_Item", 1);
        if (m is not null)
        {
            try { return m.Invoke(il2cppList, new object[] { index }); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>Lazily enumerates an il2cpp list as boxed elements via <see cref="Count"/> + <see cref="Item"/>.
    /// Elements that fail to read yield null. Safe to <c>foreach</c> over a non-<see cref="System.Collections.Generic.IEnumerable{T}"/> ZList.</summary>
    /// <param name="il2cppList">The il2cpp list instance.</param>
    public static IEnumerable<object?> Enumerate(object? il2cppList)
    {
        var n = Count(il2cppList);
        for (var i = 0; i < n; i++)
            yield return Item(il2cppList, i);
    }
}
