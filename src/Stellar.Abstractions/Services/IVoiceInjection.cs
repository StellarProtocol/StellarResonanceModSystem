using System;
using System.Threading.Tasks;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Injects audio into the game's OUTGOING team-voice stream (mixed with the live microphone).
/// Backed by a process-wide native DirectSound capture hook installed by the framework at boot.
/// Multiple sources — across all plugins — mix together into the one shared uplink.
/// </summary>
/// <remarks>
/// The uplink encoder and the capture hook are process-global, so some state here is framework-wide
/// (the DSP policy, the capture format) while each <see cref="IVoiceSource"/> is an independent voice.
/// Sources a plugin creates are auto-disposed when the plugin unloads (mirroring <see cref="IHarmonyHost"/>
/// auto-unpatch), so a plugin cannot leak a voice across a soft enable/disable cycle.
/// </remarks>
public interface IVoiceInjection
{
    /// <summary>
    /// True once the capture hook is installed AND the capture format is known (i.e. the mic is live,
    /// post-login). Audio assigned to a source before this is retained and becomes audible once true;
    /// it is simply not mixed yet. Poll or gate playback on this if you need the audio heard immediately.
    /// Read any thread; it reflects the most recent capture-format observation.
    /// </summary>
    bool Available { get; }

    /// <summary>Hook / capture-format diagnostics for status UIs and logs. Read any thread (a snapshot).</summary>
    VoiceInjectionStatus Status { get; }

    /// <summary>
    /// Decode an audio file off-thread (WAV natively; MP3/WMA/M4A via Windows Media Foundation) into a
    /// reusable clip. Does NOT require the capture format to be known — the clip keeps its source PCM and is
    /// (re)resampled to the capture format on demand. Throws on a missing file or an unsupported/corrupt
    /// encoding. Media Foundation is absent on Windows "N" editions → non-WAV will fail there (WAV still works).
    /// Call from any thread; the returned task completes on a background thread.
    /// </summary>
    /// <param name="path">Absolute path to the audio file.</param>
    Task<IVoiceClip> LoadFileAsync(string path);

    /// <summary>
    /// Build a clip from caller-supplied 16-bit interleaved PCM at the given sample rate and channel count.
    /// Copied immediately (the span need not outlive the call); resampled to the capture format on demand.
    /// Use for TTS or generated audio. Main-thread safe; returns at once (no decode).
    /// </summary>
    /// <param name="pcm">16-bit interleaved PCM samples (channel-interleaved frames).</param>
    /// <param name="sampleRate">Source sample rate in Hz (must be &gt; 0).</param>
    /// <param name="channels">Source channel count (1 = mono, 2 = stereo, …; must be &gt; 0).</param>
    IVoiceClip LoadPcm(ReadOnlySpan<short> pcm, int sampleRate, int channels);

    /// <summary>
    /// Create an independent playback voice in the mixer. Returns null if the mixer is at capacity.
    /// Dispose it to remove it from the mixer (sources a plugin leaves open are auto-disposed on plugin
    /// teardown). Typically a plugin keeps one source for looped background audio and/or uses
    /// <see cref="IVoiceSource.PlayOnce"/> for one-shots. Main-thread only.
    /// </summary>
    IVoiceSource? CreateSource();

    /// <summary>
    /// Configure the GVoice uplink DSP, auto-applied while ANY source is playing and restored to the game's
    /// defaults when all sources stop. GVoice's noise-suppression / AGC attenuate injected (non-voice) audio,
    /// so disabling them preserves music fidelity; a higher Opus bitrate improves quality (up to the 64 kbps ceiling). The uplink encoder
    /// is process-global, so this is a single global policy — the last writer wins across plugins. A null
    /// argument leaves that setting unchanged. A disposed plugin's policy becomes inert once its sources stop.
    /// Main-thread only (never called from the native mix hook).
    /// </summary>
    /// <param name="disableNoiseSuppression">Disable GVoice noise suppression while playing; null leaves it unchanged.</param>
    /// <param name="disableAgc">Disable GVoice automatic gain control while playing; null leaves it unchanged.</param>
    /// <param name="disableVad">Disable GVoice voice-activity detection while playing; null leaves it unchanged.</param>
    /// <param name="bitRateBps">Opus uplink bitrate in bits/sec while playing. Usable range 8000–64000 — 64 kbps is the GVoice uplink ceiling, so higher values do not take effect and are clamped; null leaves it unchanged.</param>
    void SetUplinkPolicy(bool? disableNoiseSuppression = null, bool? disableAgc = null,
                         bool? disableVad = null, int? bitRateBps = null);
}

/// <summary>An immutable decoded sound, reusable across sources. Holds source PCM; thread-safe to share.</summary>
public interface IVoiceClip
{
    /// <summary>Playback length at natural rate.</summary>
    TimeSpan Duration { get; }
}

/// <summary>One independent voice in the uplink mixer. Not thread-safe for concurrent mutation by the caller.</summary>
public interface IVoiceSource : IDisposable
{
    /// <summary>The clip to play. Assigning a new clip rewinds to the start and stops playback.</summary>
    IVoiceClip? Clip { get; set; }

    /// <summary>Linear gain, 0..2 (1 = unity). Clamped. Mixing is additive-saturating against the live mic.</summary>
    float Volume { get; set; }

    /// <summary>Loop the clip instead of stopping at the end.</summary>
    bool Loop { get; set; }

    /// <summary>True while this source is actively mixing. Goes false on <see cref="Stop"/> or at clip end (no loop).</summary>
    bool Playing { get; }

    /// <summary>Current playback position within the clip.</summary>
    TimeSpan Position { get; }

    /// <summary>Start (or restart) playback of the assigned clip from the beginning.</summary>
    void Play();

    /// <summary>Stop playback. Position is retained; <see cref="Play"/> restarts from the beginning.</summary>
    void Stop();

    /// <summary>Convenience one-shot: assign the clip, clear <see cref="Loop"/>, and play from the start.</summary>
    /// <param name="clip">The clip to play once.</param>
    void PlayOnce(IVoiceClip clip);
}

/// <summary>Capture-hook and format diagnostics.</summary>
/// <param name="HookInstalled">The IAT/vtable capture hook is in place.</param>
/// <param name="CaptureActive">A capture format has been observed (the mic is live).</param>
/// <param name="SampleRate">Capture format sample rate in Hz; 0 until known — typically 16000 (mono) for GVoice.</param>
/// <param name="Channels">Capture format channel count; 0 until known.</param>
/// <param name="ProbeMode">The hook arrived after GVoice init → late-install probe path (the mic was opened briefly to find the shared capture vtable).</param>
/// <param name="Detail">Human-readable hook state (counters / last error) for logs.</param>
public readonly record struct VoiceInjectionStatus(
    bool   HookInstalled,
    bool   CaptureActive,
    int    SampleRate,
    int    Channels,
    bool   ProbeMode,
    string Detail);
