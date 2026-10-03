using System;
using System.Collections.Generic;
using System.IO;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// <see cref="IReShade"/> over the Stellar ReShade bridge add-on. Polls nothing on its own: the Host calls
/// <see cref="Refresh"/> once per framework tick (ReShadeService.Refresh.cs). Every setter only queues a request the
/// add-on applies on ReShade's own present; requests made before the add-on binds are held (latest wins) and sent
/// once it does. A preset switch is additionally held until ReShade lists techniques — the add-on's host contract
/// (it ignores a preset request while there are none). Main thread only.
/// </summary>
internal sealed partial class ReShadeService : IReShade
{
    private readonly IReShadeNative _native;
    private readonly EffectDepthIndex _depth;
    private readonly IPluginLog _log;

    private readonly HashSet<string> _warnedFolders = new(StringComparer.Ordinal);

    private string? _pendingPreset;
    private (string? Effects, string? Textures)? _pendingSearchPaths;

    internal ReShadeService(IReShadeNative native, EffectDepthIndex depth, IPluginLog log)
    {
        _native = native;
        _depth = depth;
        _log = log;
    }

    public event Action? Changed;

    public bool IsAvailable => _available;

    public IReadOnlyList<ReShadeTechnique> Techniques => _techniques;

    public string? CurrentPreset => _preset;

    public bool Enabled
    {
        get => _native.IsLoaded && _native.ReadStatus().Enabled;
        set
        {
            if (!_native.IsLoaded) return;
            _native.RequestEnabled(value);
            OnRequest("enabled", value ? "on" : "off");
        }
    }

    public void SetTechnique(string effectFile, string name, bool enabled)
    {
        // An empty effect file would make the add-on match the name in EVERY effect: refused.
        if (string.IsNullOrEmpty(effectFile) || string.IsNullOrEmpty(name) || !_native.IsLoaded) return;
        _native.RequestTechnique(effectFile, name, enabled, save: true);
        _forceRead = true;
        OnRequest("technique", name);
    }

    public void SetPreset(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _pendingPreset = path;
        if (_native.IsLoaded) TrySendPendingPreset(_native.ReadStatus());
        if (_pendingPreset is not null) OnHeld("preset", path);
    }

    public void SetSearchPaths(IReadOnlyList<string> effectFolders, IReadOnlyList<string> textureFolders)
    {
        var effects = FormatFolders(effectFolders);
        var textures = FormatFolders(textureFolders);
        if (effects.Count == 0 && textures.Count == 0) return;
        if (effects.Count > 0)
        {
            _depth.SetSearchPaths(effects);
            _depthDirty = true;
            _forceRead = true;
        }
        _pendingSearchPaths = (Join(effects), Join(textures));
        TrySendPendingSearchPaths();
        if (_pendingSearchPaths is not null) OnHeld("search paths", _pendingSearchPaths.Value.Effects ?? "");
    }

    private void TrySendPendingSearchPaths()
    {
        if (_pendingSearchPaths is not { } paths || !_native.IsLoaded) return;
        _pendingSearchPaths = null;
        _native.RequestSearchPaths(paths.Effects, paths.Textures);
        OnRequest("search paths", paths.Effects ?? "");
    }

    private void TrySendPendingPreset(ReShadeNativeStatus status)
    {
        if (_pendingPreset is not { } path || !status.Ready || status.Loading || status.TechniqueCount <= 0) return;
        _pendingPreset = null;
        _native.RequestPreset(path);
        _forceRead = true;
        OnRequest("preset", path);
    }

    private List<string> FormatFolders(IReadOnlyList<string> folders)
    {
        var formatted = new List<string>(folders.Count);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || folder.Contains(';') || !Path.IsPathRooted(folder))
            {
                if (_warnedFolders.Add(folder ?? ""))
                    _log.Warning($"[ReShade] search path skipped (must be an absolute folder without ';'): '{folder}'");
                continue;
            }
            formatted.Add(ReShadeSearchPath.FormatRecursive(folder));
        }
        return formatted;
    }

    private static string? Join(List<string> paths) => paths.Count == 0 ? null : string.Join(';', paths);

    private void RaiseChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                _log.Warning($"[ReShade] Changed handler threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
