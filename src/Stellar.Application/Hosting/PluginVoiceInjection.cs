using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Hosting;

/// <summary>
/// One plugin's view of the shared <see cref="IVoiceInjection"/> mixer. Everything is forwarded to the shared service
/// except <see cref="CreateSource"/>, which records the sources this plugin creates so <see cref="ReleaseAll"/>
/// (plugin unload) disposes any the plugin left open — mirroring <see cref="IHarmonyHost"/> auto-unpatch, so a plugin
/// can never leak a voice across a soft enable/disable cycle. The uplink DSP policy is process-global and
/// last-writer-wins, so it is forwarded as-is; a disposed plugin's policy becomes inert once its sources stop (the
/// shared service auto-restores when nothing is playing). Main thread.
/// </summary>
internal sealed class PluginVoiceInjection : IVoiceInjection
{
    private readonly IVoiceInjection _shared;
    private readonly List<IVoiceSource> _sources = new();

    public PluginVoiceInjection(IVoiceInjection shared) => _shared = shared;

    public bool Available => _shared.Available;
    public VoiceInjectionStatus Status => _shared.Status;

    public Task<IVoiceClip> LoadFileAsync(string path) => _shared.LoadFileAsync(path);

    public IVoiceClip LoadPcm(ReadOnlySpan<short> pcm, int sampleRate, int channels)
        => _shared.LoadPcm(pcm, sampleRate, channels);

    public IVoiceSource? CreateSource()
    {
        // Drop handles the plugin already disposed so the tracked list doesn't grow across a long session.
        _sources.RemoveAll(static s => s is TrackedSource { Disposed: true });
        var inner = _shared.CreateSource();
        if (inner == null) return null;
        var tracked = new TrackedSource(inner);
        _sources.Add(tracked);
        return tracked;
    }

    public void SetUplinkPolicy(bool? disableNoiseSuppression = null, bool? disableAgc = null,
                                bool? disableVad = null, int? bitRateBps = null)
        => _shared.SetUplinkPolicy(disableNoiseSuppression, disableAgc, disableVad, bitRateBps);

    /// <summary>Disposes every source this plugin still holds open (plugin unload). Idempotent per source.</summary>
    internal void ReleaseAll()
    {
        if (_sources.Count == 0) return;
        var snapshot = new List<IVoiceSource>(_sources);
        _sources.Clear();
        foreach (var s in snapshot)
        {
            try { s.Dispose(); }
            catch { /* a release must not stop at one source */ }
        }
    }

    // Thin wrapper that records disposal so ReleaseAll can skip handles the plugin already freed (the underlying
    // VoiceSource.Dispose is itself idempotent, so a double dispose is harmless either way).
    private sealed class TrackedSource : IVoiceSource
    {
        private readonly IVoiceSource _inner;
        public bool Disposed { get; private set; }

        public TrackedSource(IVoiceSource inner) => _inner = inner;

        public IVoiceClip? Clip { get => _inner.Clip; set => _inner.Clip = value; }
        public float Volume { get => _inner.Volume; set => _inner.Volume = value; }
        public bool Loop { get => _inner.Loop; set => _inner.Loop = value; }
        public bool Playing => _inner.Playing;
        public TimeSpan Position => _inner.Position;
        public void Play() => _inner.Play();
        public void Stop() => _inner.Stop();
        public void PlayOnce(IVoiceClip clip) => _inner.PlayOnce(clip);

        public void Dispose()
        {
            Disposed = true;
            _inner.Dispose();
        }
    }
}
