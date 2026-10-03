using System.Collections.Generic;
using Stellar.Infrastructure.Rendering;

namespace Stellar.Application.Tests.ReShade;

/// <summary>Scriptable <see cref="IReShadeNative"/>: tests set the bridge's snapshot and read back the requests.</summary>
internal sealed class FakeReShadeNative : IReShadeNative
{
    public bool IsLoaded { get; set; } = true;
    public bool Ready { get; set; } = true;
    public bool Loading { get; set; }
    public bool EffectsEnabled { get; set; } = true;
    public int Frames { get; set; }
    public string? Preset { get; set; }
    public List<(string Name, string Effect, bool Enabled)> Techniques { get; } = new();
    public List<string> Requests { get; } = new();
    public int TechniqueReads { get; private set; }

    public void Add(string name, string effect, bool enabled = true) => Techniques.Add((name, effect, enabled));
    public void NextFrame() => Frames++;

    public ReShadeNativeStatus ReadStatus() => new(Ready, Loading, Frames, Techniques.Count, EffectsEnabled);

    public bool TryGetTechnique(int index, out string name, out string effectFile, out bool enabled)
    {
        TechniqueReads++;
        if (index < 0 || index >= Techniques.Count)
        {
            name = effectFile = "";
            enabled = false;
            return false;
        }
        (name, effectFile, enabled) = Techniques[index];
        return true;
    }

    public string? GetPreset() => Preset;
    public void RequestEnabled(bool on) => Requests.Add($"enabled {on}");
    public void RequestTechnique(string? effectFile, string name, bool on, bool save) =>
        Requests.Add($"technique {effectFile ?? "<any>"}/{name} on={on} save={save}");
    public void RequestPreset(string path) => Requests.Add($"preset {path}");
    public void RequestSearchPaths(string? effects, string? textures) =>
        Requests.Add($"paths effects={effects ?? "<unchanged>"} textures={textures ?? "<unchanged>"}");
}
