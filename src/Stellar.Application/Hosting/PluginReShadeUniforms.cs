using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Hosting;

/// <summary>
/// One plugin's view of the shared <see cref="IReShadeUniforms"/>: it remembers which overrides this plugin holds, so
/// <see cref="ClearUniformOverrides"/> removes only those and <see cref="ReleaseAll"/> (plugin unload) removes whatever
/// is left — a plugin's overrides never outlive it. Main thread only.
/// </summary>
internal sealed class PluginReShadeUniforms : IReShadeUniforms
{
    private readonly IReShadeUniforms _shared;
    private readonly HashSet<(string Effect, string Variable)> _held = new();

    internal PluginReShadeUniforms(IReShadeUniforms shared) => _shared = shared;

    /// <inheritdoc/>
    public bool SetUniformOverride(string? effectFile, string variable, string? value)
    {
        var ok = _shared.SetUniformOverride(effectFile, variable, value);
        if (!ok || string.IsNullOrWhiteSpace(variable)) return ok;
        var key = (effectFile ?? "", variable);
        if (string.IsNullOrEmpty(value)) _held.Remove(key);
        else _held.Add(key);
        return ok;
    }

    /// <inheritdoc/>
    public void ClearUniformOverrides() => ReleaseAll();

    /// <summary>Removes every override this plugin still holds (plugin unload).</summary>
    internal void ReleaseAll()
    {
        if (_held.Count == 0) return;
        var held = new List<(string Effect, string Variable)>(_held);
        _held.Clear();
        foreach (var (effect, variable) in held)
        {
            try { _shared.SetUniformOverride(effect.Length == 0 ? null : effect, variable, null); }
            catch (Exception) { /* the shared service never throws; a release must not stop at one entry */ }
        }
    }
}
