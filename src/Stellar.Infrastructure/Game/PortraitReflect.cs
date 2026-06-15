using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Tiny reflection helpers over live Il2Cpp proxy objects — get/set a property-or-field and invoke a method by
/// name, swallowing the inner <see cref="TargetInvocationException"/> so callers get the real message. Used by
/// the portrait pipeline to drive the game's <c>ZModel2RTData</c> / <c>ZModel2RT</c> without compile-time refs.
/// </summary>
internal static class PortraitReflect
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    public static object? Get(object target, string name)
    {
        var t = target.GetType();
        var p = t.GetProperty(name, Any);
        if (p?.CanRead == true) return p.GetValue(target);
        return t.GetField(name, Any)?.GetValue(target);
    }

    public static object? GetStatic(Type t, string name)
    {
        var p = t.GetProperty(name, AnyStatic);
        if (p?.CanRead == true) return p.GetValue(null);
        return t.GetField(name, AnyStatic)?.GetValue(null);
    }

    public static bool Set(object target, string name, object? value)
    {
        var t = target.GetType();
        var p = t.GetProperty(name, Any);
        if (p?.CanWrite == true) { p.SetValue(target, value); return true; }
        var f = t.GetField(name, Any);
        if (f != null) { f.SetValue(target, value); return true; }
        return false;
    }

    public static object? Invoke(object target, string name, params object[] args)
    {
        var m = target.GetType().GetMethod(name, Any, null, Types(args), null)
                ?? target.GetType().GetMethod(name, Any);
        return m?.Invoke(target, args);
    }

    public static object? InvokeStatic(Type t, string name, params object[] args)
    {
        var m = t.GetMethod(name, AnyStatic, null, Types(args), null) ?? t.GetMethod(name, AnyStatic);
        return m?.Invoke(null, args);
    }

    public static string Unwrap(Exception ex)
    {
        var inner = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;
        var s = inner.ToString();
        return s.Length > 500 ? s[..500] : s;
    }

    private static Type[] Types(object[] args)
    {
        var ts = new Type[args.Length];
        for (var i = 0; i < args.Length; i++) ts[i] = args[i]?.GetType() ?? typeof(object);
        return ts;
    }
}
