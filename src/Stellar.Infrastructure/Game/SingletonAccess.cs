using System;
using System.Reflection;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Game;

/// <summary>Reads a game singleton's <c>Instance</c> only when its static <c>IsCreated</c> says it exists (a
/// <c>ZSingleton&lt;T&gt;.Instance</c> getter can construct one — docs/il2cpp-probing-safety.md). Resolved once per type.</summary>
internal sealed class SingletonAccess
{
    private MethodInfo? _isCreated;
    private MethodInfo? _instance;

    public bool Resolve(Type? t)
    {
        if (_instance is not null) return true;
        if (t is null) return false;
        _isCreated = Static(t, "IsCreated");
        _instance = Static(t, "Instance");
        return _instance is not null;
    }

    public object? Get()
    {
        if (_instance is null) return null;
        try
        {
            if (_isCreated is not null && _isCreated.Invoke(null, null) is not true) return null;
            return _instance.Invoke(null, null);
        }
        catch { return null; }
    }

    private static MethodInfo? Static(Type t, string name) =>
        StellarInterop.FindPropertyUp(t, name)?.GetGetMethod(nonPublic: true) is { IsStatic: true } g ? g : null;
}
