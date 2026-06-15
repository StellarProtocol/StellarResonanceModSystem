using System;
using System.Collections.Concurrent;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Resolves an <c>MLString</c> handle (or string key) to its current-language
/// display string. Encapsulates the discovery of <c>LocalizationMgr.Instance</c>
/// + <c>Panda.MLStringPoolBridge.GetString</c>.
///
/// <para>
/// Recon notes (confirmed during Iter 1):
/// <list type="bullet">
///   <item><c>Bokura.Table.ReadProxy.ReadMLString</c> already returns a resolved
///         <see cref="string"/> at row-read time — most <c>get_Name</c> /
///         <c>get_Desc</c> getters on table rows therefore expose plain strings.
///         This resolver is reserved for the cases where a raw MLString handle
///         is exposed by a getter (e.g. nested KV tables).</item>
///   <item><c>Panda.MLStringPoolBridge.AddString</c> / <c>GetString</c> are the
///         entry points if a raw handle needs resolution.</item>
///   <item>Missing keys are cached so a stale lookup doesn't reflect every frame.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class PandaMLStringResolver
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    private bool _bootstrapped;
    private MethodInfo? _bridgeGetString;            // Panda.MLStringPoolBridge.GetString(...)
    private readonly ConcurrentDictionary<object, bool> _missCache = new();
    private bool _firstMissLogged;

    public PandaMLStringResolver(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    /// <summary>
    /// Resolve <paramref name="mlStringOrKey"/> to its current-language display
    /// string. Returns the empty string for misses so callers can render
    /// deterministically without a null check.
    /// </summary>
    public string Resolve(object? mlStringOrKey)
    {
        if (mlStringOrKey is null)
        {
            return string.Empty;
        }

        // Already a resolved string? Pass through — most table getters return
        // strings directly because ReadProxy.ReadMLString resolves at row-read time.
        if (mlStringOrKey is string s)
        {
            return s;
        }

        EnsureBootstrap();
        if (_bridgeGetString is null)
        {
            return CacheMiss(mlStringOrKey);
        }

        try
        {
            var result = _bridgeGetString.Invoke(null, new[] { mlStringOrKey });
            if (result is string resolved && !string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }
        }
        catch
        {
            // Fall through — treat as miss.
        }

        return CacheMiss(mlStringOrKey);
    }

    private string CacheMiss(object key)
    {
        if (_missCache.TryAdd(key, true) && !_firstMissLogged)
        {
            _firstMissLogged = true;
            _log.Info($"[MLStringResolver] first miss for key {key.GetType().FullName}");
        }
        return string.Empty;
    }

    private void EnsureBootstrap()
    {
        if (_bootstrapped)
        {
            return;
        }
        _bootstrapped = true;

        var bridge = _typeRegistry.FindType("Panda.MLStringPoolBridge")
                     ?? _typeRegistry.FindType("ZResource.MLStringPoolBridge");
        if (bridge is null)
        {
            _log.Warning("[MLStringResolver] MLStringPoolBridge type not found; resolver will treat raw handles as misses");
            return;
        }

        // GetString is a static method that accepts the raw MLString handle and
        // returns the current-language string. Cpp2IL recon showed the surface as
        //   public static string GetString(object handle)
        // with a sibling AddString. Pick the first one that takes a single arg.
        foreach (var m in bridge.GetMethods(AnyStatic))
        {
            if (m.Name != "GetString")
            {
                continue;
            }
            if (m.GetParameters().Length != 1)
            {
                continue;
            }
            if (m.ReturnType != typeof(string))
            {
                continue;
            }
            _bridgeGetString = m;
            break;
        }

        if (_bridgeGetString is null)
        {
            _log.Warning($"[MLStringResolver] {bridge.FullName}.GetString(*) not found; raw-handle resolution disabled");
        }
    }
}
