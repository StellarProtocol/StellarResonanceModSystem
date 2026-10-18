using System;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Framework implementation of <see cref="IVoiceInjection"/>: a thin facade over the process-global native capture
/// hook (<see cref="VoiceCaptureHook"/>) and mixer (<see cref="VoiceMixer"/>), plus a main-thread maintainer
/// (<see cref="Tick"/>) that (a) re-converts every live clip to the current capture format off-thread, (b) applies the
/// uplink DSP while any source plays and restores it when all stop, and (c) runs the hot-reload probe fallback.
/// Constructed once at boot (<c>Wiring.Voice.cs</c>); the per-plugin façade (<c>PluginVoiceInjection</c>) wraps it.
/// </summary>
internal sealed class VoiceInjectionService : IVoiceInjection
{
    private readonly Action<string> _log;

    // Uplink-DSP policy (last-writer-wins, process-global). null = leave that GVoice setting untouched.
    private bool? _disableNs, _disableAgc, _disableVad;
    private int? _bitRateBps;

    // DSP apply/restore state (main thread only). _lastApplyMs re-arms the periodic re-apply (GVoice re-forces some
    // settings in OnMicOptBegin), so we re-push every ~5 s while active.
    private bool _dspActive;
    private long _lastApplyMs;
    private const long ReapplyMs = 5000;

    // Boot-install bookkeeping for the hot-reload probe fallback.
    private bool _installed, _probed;
    private long _installMs;
    private const long ProbeGraceMs = 2500;   // only probe if GVoice was already loaded and no buffer showed by now

    public VoiceInjectionService(Action<string> log) => _log = log;

    public bool Available => VoiceCaptureHook.LockPatched && VoiceCaptureHook.FormatSupported;

    public VoiceInjectionStatus Status => new(
        HookInstalled: VoiceCaptureHook.IatPatched,
        CaptureActive: VoiceCaptureHook.FormatSupported,
        SampleRate:    VoiceCaptureHook.FormatRate,
        Channels:      VoiceCaptureHook.FormatChannels,
        ProbeMode:     VoiceCaptureHook.ProbeMode,
        Detail:        $"{VoiceCaptureHook.HookLine} · {VoiceCaptureHook.FormatLine} · {VoiceCaptureHook.CounterLine} " +
                       $"· sources {VoiceMixer.ActiveCount}/{VoiceMixer.Capacity} · dsp {(_dspActive ? "on" : "off")}");

    public Task<IVoiceClip> LoadFileAsync(string path)
        => Task.Run<IVoiceClip>(() => new VoiceClip(AudioDecoder.Decode(path)));

    public IVoiceClip LoadPcm(ReadOnlySpan<short> pcm, int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), "sample rate must be > 0");
        if (channels   <= 0) throw new ArgumentOutOfRangeException(nameof(channels), "channels must be > 0");
        var copy = pcm.ToArray();   // copied immediately — the span need not outlive the call
        return new VoiceClip(new DecodedAudio(copy, sampleRate, channels, "PCM"));
    }

    public IVoiceSource? CreateSource()
    {
        var slot = VoiceMixer.ClaimSlot();
        if (slot == null)
        {
            _log($"[Voice] CreateSource: mixer at capacity ({VoiceMixer.Capacity}) — source not created");
            return null;
        }
        return new VoiceSource(slot);
    }

    public void SetUplinkPolicy(bool? disableNoiseSuppression = null, bool? disableAgc = null,
                                bool? disableVad = null, int? bitRateBps = null)
    {
        if (disableNoiseSuppression.HasValue) _disableNs  = disableNoiseSuppression;
        if (disableAgc.HasValue)              _disableAgc = disableAgc;
        if (disableVad.HasValue)              _disableVad = disableVad;
        if (bitRateBps.HasValue)              _bitRateBps = Math.Clamp(bitRateBps.Value, 8000, VoiceDsp.MaxBitRate);
        // Take effect immediately if audio is already playing; otherwise the next apply transition picks it up.
        if (_dspActive) ApplyPolicy();
    }

    /// <summary>Records the boot install result (and when), so the maintainer can run the probe fallback on hot-reload.</summary>
    public void NotifyInstalled(string result)
    {
        _installed = true;
        _installMs = Environment.TickCount64;
        _log($"[Voice] capture hook install: {result}");
    }

    /// <summary>
    /// Maintainer tick — main thread (framework Update). Runs the probe fallback, re-converts live clips to the live
    /// capture format off-thread, and drives the uplink-DSP apply/restore from the mixer's aggregate play state.
    /// Non-throwing: a failure here must never reach the game loop.
    /// </summary>
    public void Tick()
    {
        try
        {
            MaybeProbe();
            ReconvertLiveClips();
            DriveDsp();
        }
        catch (Exception ex)
        {
            _log($"[Voice] maintainer tick failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Hot-reload fallback only: GVoice was already loaded (hence likely already created its capture buffer before our
    // IAT hook), the IAT hook is in but Lock never got patched after a grace window → probe for the shared vtable.
    // Never fires on the normal boot path (Install force-loads the DLL first, so InstalledWhileGVoiceLoaded is false).
    private void MaybeProbe()
    {
        if (_probed || !_installed) return;
        if (!VoiceCaptureHook.InstalledWhileGVoiceLoaded) return;
        if (!VoiceCaptureHook.IatPatched || VoiceCaptureHook.LockPatched) return;
        if (Environment.TickCount64 - _installMs < ProbeGraceMs) return;
        _probed = true;
        _log($"[Voice] no capture buffer seen since attach — running probe install (mic opens briefly)");
        _log($"[Voice] probe install: {VoiceCaptureHook.ProbeInstall()}");
    }

    // Convert every clip referenced by a live source for the current format version (off-thread). The hot path only
    // ever reads a finished buffer; BeginConvert dedups a clip shared across slots or across ticks.
    private void ReconvertLiveClips()
    {
        if (!VoiceCaptureHook.FormatSupported) return;
        int rate = VoiceCaptureHook.FormatRate, ch = VoiceCaptureHook.FormatChannels, ver = VoiceCaptureHook.FormatVersion;
        foreach (var slot in VoiceMixer.Slots)
        {
            if (slot.InUse == 0) continue;
            var clip = slot.Clip;
            if (clip == null || clip.ConvertedVersion == ver) continue;
            if (!clip.BeginConvert()) continue;
            Task.Run(() =>
            {
                try
                {
                    var old = clip.Converted;
                    var neu = AudioDecoder.Convert(clip.Source, rate, ch);
                    VoiceMixer.ReplaceConverted(clip, old, neu);
                    clip.EndConvert(ver);
                }
                catch (Exception ex)
                {
                    clip.AbortConvert();   // leave the version unset so the next tick retries
                    _log($"[Voice] clip conversion failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }
    }

    // Apply the policy while anything plays, restore GVoice's defaults when nothing does, and re-push periodically
    // while active. Only meaningful once GVoice is live (Lock patched). Main thread.
    private void DriveDsp()
    {
        bool anyPlaying = VoiceCaptureHook.LockPatched && VoiceMixer.AnyPlaying;
        if (anyPlaying && !_dspActive)
        {
            _dspActive = true;
            ApplyPolicy();
        }
        else if (!anyPlaying && _dspActive)
        {
            _dspActive = false;
            RestorePolicy();
        }
        else if (anyPlaying && _dspActive && Environment.TickCount64 - _lastApplyMs >= ReapplyMs)
        {
            ApplyPolicy();
        }
    }

    private void ApplyPolicy()
    {
        _lastApplyMs = Environment.TickCount64;
        if (_disableNs.HasValue)  VoiceDsp.Invoke(VoiceDsp.CmdEnableNs,  _disableNs.Value  ? 0 : 1);
        if (_disableAgc.HasValue) VoiceDsp.Invoke(VoiceDsp.CmdEnableAgc, _disableAgc.Value ? 0 : 1);
        if (_disableVad.HasValue) VoiceDsp.Invoke(VoiceDsp.CmdEnableVad, _disableVad.Value ? 0 : 1);
        if (_bitRateBps.HasValue) VoiceDsp.SetBitRate(_bitRateBps.Value);
    }

    // Restore the game defaults (NS/AGC/VAD on, bitrate 32000) for only the settings this policy actually touched.
    private void RestorePolicy()
    {
        if (_disableNs.HasValue)  VoiceDsp.Invoke(VoiceDsp.CmdEnableNs,  1);
        if (_disableAgc.HasValue) VoiceDsp.Invoke(VoiceDsp.CmdEnableAgc, 1);
        if (_disableVad.HasValue) VoiceDsp.Invoke(VoiceDsp.CmdEnableVad, 1);
        if (_bitRateBps.HasValue) VoiceDsp.SetBitRate(VoiceDsp.DefaultBitRate);
    }
}
