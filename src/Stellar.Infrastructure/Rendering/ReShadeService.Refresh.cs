using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Rendering;

/// <summary>The per-tick poll: cheap while nothing moves (one status read), re-reads the technique list when the
/// add-on's status moved, after a request, or every <see cref="ReReadEveryTicks"/> ticks (a toggle made in ReShade's
/// own overlay changes nothing the status shows), and resolves at most <see cref="DepthResolvesPerTick"/> effect files'
/// depth use per tick so a first listing of a large shader pack never stalls a frame. An effect not yet resolved reads
/// as using depth. Nothing is allocated unless something changed.</summary>
internal sealed partial class ReShadeService
{
    internal const int ReReadEveryTicks = 15;
    internal const int DepthResolvesPerTick = 4;

    private readonly record struct RawTechnique(string Name, string EffectFile, bool Enabled);

    private readonly List<RawTechnique> _raw = new();
    private IReadOnlyList<ReShadeTechnique> _techniques = Array.Empty<ReShadeTechnique>();
    private ReShadeNativeStatus _status;
    private bool _hasStatus;
    private bool _available;
    private string? _preset;
    private bool _forceRead;
    private bool _depthDirty;
    private int _ticksSinceRead;

    /// <summary>Called by the Host once per framework tick. Raises <see cref="Changed"/> when availability, the
    /// technique list (incl. depth use) or the preset changed.</summary>
    internal void Refresh()
    {
        if (!_native.IsLoaded)
        {
            Lose();
            return;
        }
        if (!_hasStatus) OnBound();
        TrySendPendingSearchPaths();
        var status = _native.ReadStatus();
        if (_hasStatus && status.Frames == _status.Frames) return;

        var changed = Observe(status);
        TrySendPendingPreset(status);
        if (changed) RaiseChanged();
    }

    private bool Observe(ReShadeNativeStatus status)
    {
        var moved = !_hasStatus || status.Ready != _status.Ready || status.Loading != _status.Loading
                    || status.TechniqueCount != _status.TechniqueCount;
        if (_hasStatus && _status.Loading && !status.Loading)
        {
            _depth.MarkStale(); // ReShade reloaded: re-check the effect files it compiled
            _depthDirty = true;
        }
        _hasStatus = true;
        _status = status;
        var available = status.Ready && !status.Loading;
        var changed = available != _available;
        _available = available;

        var listChanged = false;
        if (moved || _forceRead || ++_ticksSinceRead >= ReReadEveryTicks)
        {
            _ticksSinceRead = 0;
            _forceRead = false;
            listChanged = ReadTechniques(available ? status.TechniqueCount : 0);
            changed |= ReadPreset();
        }
        if (listChanged)
        {
            _depthDirty = true;
            OnTechniquesRead(_raw.Count);
        }
        var depthChanged = PumpDepth();
        if (listChanged || depthChanged || changed)
        {
            Publish();
            return true;
        }
        return false;
    }

    private bool ReadTechniques(int count)
    {
        var changed = false;
        var read = 0;
        for (; read < count; read++)
        {
            if (!_native.TryGetTechnique(read, out var name, out var effect, out var enabled)) break;
            var technique = new RawTechnique(name, effect, enabled);
            if (read >= _raw.Count)
            {
                _raw.Add(technique);
                changed = true;
            }
            else if (!_raw[read].Equals(technique))
            {
                _raw[read] = technique;
                changed = true;
            }
        }
        if (_raw.Count > read)
        {
            _raw.RemoveRange(read, _raw.Count - read);
            changed = true;
        }
        return changed;
    }

    private bool ReadPreset()
    {
        var preset = _native.GetPreset();
        if (string.Equals(preset, _preset, StringComparison.Ordinal)) return false;
        _preset = preset;
        OnPresetSeen(preset);
        return true;
    }

    private bool PumpDepth()
    {
        if (!_depthDirty) return false;
        var budget = DepthResolvesPerTick;
        var pending = false;
        var changed = false;
        foreach (var technique in _raw)
        {
            if (!_depth.NeedsWork(technique.EffectFile)) continue;
            if (budget == 0)
            {
                pending = true;
                break;
            }
            var before = _depth.Known(technique.EffectFile);
            changed |= _depth.Resolve(technique.EffectFile) != before;
            budget--;
        }
        _depthDirty = pending;
        return changed;
    }

    private void Publish()
    {
        if (_raw.Count == 0)
        {
            _techniques = Array.Empty<ReShadeTechnique>();
            return;
        }
        var list = new ReShadeTechnique[_raw.Count];
        for (var i = 0; i < list.Length; i++)
        {
            var raw = _raw[i];
            list[i] = new ReShadeTechnique(raw.Name, raw.EffectFile, raw.Enabled, _depth.Known(raw.EffectFile) ?? true);
        }
        _techniques = list;
    }

    private void Lose()
    {
        var hadState = _available || _raw.Count > 0 || _preset is not null;
        _hasStatus = false;
        _available = false;
        _raw.Clear();
        _techniques = Array.Empty<ReShadeTechnique>();
        _preset = null;
        if (hadState) RaiseChanged();
    }
}
