using System;
using System.Linq.Expressions;
using System.Reflection;
namespace Stellar.Infrastructure.Game;

/// <summary>Compiled property accessors for per-frame reflection (the position hold). Falls back to
/// <see cref="PropertyInfo"/> calls if compilation fails. Stateless.</summary>
internal static class FastAccess
{
    public static Func<object, T>? Getter<T>(PropertyInfo? p)
    {
        if (p?.GetGetMethod(nonPublic: true) is null || p.DeclaringType is null) return null;
        try
        {
            var o = Expression.Parameter(typeof(object), "o");
            var body = Expression.Convert(Expression.Property(Expression.Convert(o, p.DeclaringType), p), typeof(T));
            return Expression.Lambda<Func<object, T>>(body, o).Compile();
        }
        catch
        {
            return o => (T)p.GetValue(o)!;
        }
    }

    public static Action<object, T>? Setter<T>(PropertyInfo? p)
    {
        if (p?.GetSetMethod(nonPublic: true) is null || p.DeclaringType is null) return null;
        try
        {
            var o = Expression.Parameter(typeof(object), "o");
            var v = Expression.Parameter(typeof(T), "v");
            var body = Expression.Assign(Expression.Property(Expression.Convert(o, p.DeclaringType), p), Expression.Convert(v, p.PropertyType));
            return Expression.Lambda<Action<object, T>>(body, o, v).Compile();
        }
        catch
        {
            return (o, value) => p.SetValue(o, value);
        }
    }

    /// <summary>Compiled call of a parameterless instance method (e.g. <c>ZModel.GetLuaAttrActionInfoPassedTime()</c>).</summary>
    public static Func<object, TResult>? Func0<TResult>(MethodInfo? m)
    {
        if (m is null || m.IsStatic || m.DeclaringType is null || m.GetParameters().Length != 0) return null;
        try
        {
            var o = Expression.Parameter(typeof(object), "o");
            var call = Expression.Call(Expression.Convert(o, m.DeclaringType), m);
            return Expression.Lambda<Func<object, TResult>>(Expression.Convert(call, typeof(TResult)), o).Compile();
        }
        catch
        {
            return o => (TResult)m.Invoke(o, null)!;
        }
    }

    /// <summary>Compiled call of a one-argument instance method (e.g. <c>ZEntityMgr.GetEntity(long)</c>).</summary>
    public static Func<object, TArg, TResult>? Func1<TArg, TResult>(MethodInfo? m)
    {
        if (m is null || m.IsStatic || m.DeclaringType is null || m.GetParameters().Length != 1) return null;
        try
        {
            var o = Expression.Parameter(typeof(object), "o");
            var a = Expression.Parameter(typeof(TArg), "a");
            var arg = Expression.Convert(a, m.GetParameters()[0].ParameterType);
            var call = Expression.Call(Expression.Convert(o, m.DeclaringType), m, arg);
            return Expression.Lambda<Func<object, TArg, TResult>>(Expression.Convert(call, typeof(TResult)), o, a).Compile();
        }
        catch
        {
            return (o, value) => (TResult)m.Invoke(o, new object?[] { value })!;
        }
    }

    /// <summary>Compiled call of a one-argument static method (e.g. the extension <c>GetAttrGoPosition(ZModel)</c>).</summary>
    public static Func<TArg, TResult>? StaticFunc1<TArg, TResult>(MethodInfo? m)
    {
        if (m is null || !m.IsStatic || m.GetParameters().Length != 1) return null;
        try
        {
            var a = Expression.Parameter(typeof(TArg), "a");
            var call = Expression.Call(m, Expression.Convert(a, m.GetParameters()[0].ParameterType));
            return Expression.Lambda<Func<TArg, TResult>>(Expression.Convert(call, typeof(TResult)), a).Compile();
        }
        catch
        {
            return value => (TResult)m.Invoke(null, new object?[] { value })!;
        }
    }
}
