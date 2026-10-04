using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// <see cref="IReShadeUniforms"/> for the whole framework (plugins see a per-plugin view that removes their overrides on
/// unload). Validates the value form itself (<see cref="UniformOverrideValue"/>), sends to a bound 1.1.0+ add-on at once,
/// and holds overrides set while no add-on is bound, sending them when it binds. The add-on keeps overrides process-wide
/// and re-applies them after reloads, preset switches and runtime re-creation, so nothing is re-sent per tick.
/// </summary>
internal sealed partial class ReShadeService : IReShadeUniforms
{
    private readonly Dictionary<(string Effect, string Variable), string> _uniformOverrides = new();

    private IReShadeUniformNative? UniformNative => _native as IReShadeUniformNative;

    /// <inheritdoc/>
    public bool SetUniformOverride(string? effectFile, string variable, string? value)
    {
        if (string.IsNullOrWhiteSpace(variable)) return false;
        var remove = string.IsNullOrEmpty(value);
        if (!remove && !UniformOverrideValue.IsValid(value!)) return false;
        var key = (effectFile ?? "", variable);
        if (!_native.IsLoaded)
        {
            if (remove) _uniformOverrides.Remove(key);
            else _uniformOverrides[key] = value!;
            return true;   // held: sent when the add-on binds (SendHeldUniformOverrides)
        }
        if (UniformNative is not { UniformOverridesSupported: true } native) return false;
        var ok = native.SetUniformOverride(NullIfEmpty(effectFile), variable, remove ? null : value) == 1;
        if (remove) _uniformOverrides.Remove(key);
        else if (ok) _uniformOverrides[key] = value!;
        OnRequest("uniform", $"{key.Item1}/{variable}={value ?? "<remove>"} ok={ok}");
        return ok;
    }

    /// <inheritdoc/>
    public void ClearUniformOverrides()
    {
        _uniformOverrides.Clear();
        if (_native.IsLoaded && UniformNative is { UniformOverridesSupported: true } native) native.ClearUniformOverrides();
    }

    // Called when the add-on (re)binds: the held overrides go out once. One the add-on refuses is dropped and logged.
    private void SendHeldUniformOverrides()
    {
        if (_uniformOverrides.Count == 0) return;
        if (UniformNative is not { UniformOverridesSupported: true } native)
        {
            _log.Warning($"[ReShade] {_uniformOverrides.Count} uniform override(s) not applied: the bridge add-on is older than 1.1.0");
            return;
        }
        List<(string Effect, string Variable)>? refused = null;
        foreach (var ((effect, variable), value) in _uniformOverrides)
        {
            if (native.SetUniformOverride(NullIfEmpty(effect), variable, value) == 1) continue;
            (refused ??= new List<(string, string)>()).Add((effect, variable));
            _log.Warning($"[ReShade] uniform override refused by the add-on: {effect}/{variable}={value}");
        }
        if (refused is null) return;
        foreach (var key in refused) _uniformOverrides.Remove(key);
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
