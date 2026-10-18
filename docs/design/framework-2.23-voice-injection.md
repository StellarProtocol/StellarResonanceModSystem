# Design + Plan: Voice Injection service (framework 2.23)

- **Status:** Phase 0 (design) — this doc. Phase 1 (framework surface + native hook) and Phase 2 (Experiment
  plugin migrated onto the API) NOT started.
- **Date:** 2026-10-18
- **Area:** `Stellar.Abstractions`, `Stellar.Application`, `Stellar.Infrastructure`, `Stellar.Host` (+ Experiment
  plugin migration in a separate repo).
- **Baseline:** `main` (SDK **2.22.0**). Additive → ships as **2.23.0**. The `framework-v2` / `2.0.0` branch is
  dead; do not target it.
- **Prior art:** the validated plugin implementation on Experiment branch `enhance/voice-inject`
  (`VoiceCaptureHook*.cs`, `VoiceNative.cs`, `VoiceDsp.cs`, `AudioDecoder*.cs`, `Plugin.VoiceInject*.cs`).
  In-game validated 2026-10-05. Full reverse-engineering in the workspace KB `Voice-Chat-GVoice.md`.

---

## 1. Motivation

BPSR's team-voice uplink runs on **Tencent GVoice (`GCloudVoice.dll`)**, and its microphone capture is **100%
native** (a DirectSound `CAudCapDs` capture buffer) — no `UnityEngine.Microphone`, no managed PCM feed, so
**Harmony cannot reach the audio**. The only way to put custom audio into team voice is a raw native hook on
DirectSound's capture-buffer `Lock`, mixing PCM into the buffer before GVoice reads it. That hook, plus file
decoding, resampling and the GVoice uplink-DSP tuning, was built and validated once in `StellarExperimentPlugin`.

Promoting it to the framework gives every plugin a safe, shared way to **play sound into team voice** without
re-deriving a fragile native hook (boot-ordering, IAT-by-ordinal, process-wide vtable sharing, allocation-free
hot path). It also gives us a single installation point that runs **before the game's login-time
`GCloudVoice_Init`** — the ordering the plugin has to fight for with a force-load + probe fallback.

## 2. Scope

**In:**
- A new plugin-facing service `IPluginServices.Voice` (`IVoiceInjection`) — §3.
- A **multi-source mixer**: multiple plugins (and multiple sounds within a plugin) mix simultaneously into the
  one shared mic uplink. Each voice is an independent `IVoiceSource` (clip, volume, loop, play/stop, position).
- **File + raw-PCM** input: load WAV / MP3 / WMA / M4A (Media Foundation), or submit a 16-bit PCM buffer (for
  TTS / generated audio). Both decode/resample to the live capture format.
- A **process-global uplink DSP policy** (disable GVoice NS/AGC/VAD, raise Opus bitrate) auto-applied while any
  source is playing and restored when all stop.
- Native hook installed from `BootstrapPlugin` at boot (title screen, before login).
- Migrate Experiment's Voice Inject to consume the service; delete its private native copy.

**Out:**
- Live push-streaming of continuously produced PCM (ring-buffered feed). Deferred — see §8. `LoadPcm` covers
  pre-rendered/generated buffers, which satisfies TTS and sound-effect use today.
- Downlink / receive-side audio (hearing other players) — a different GVoice path, not addressed here.
- Any TTS engine. The service plays audio; producing TTS PCM is the plugin's job.

## 3. New framework surface (all additive → non-breaking)

All of this lives in `Stellar.Abstractions` (interfaces + records, XML-documented — CS1591 is an error there;
no `GUILayout`/Unity/BepInEx tokens). Mirror the `IHarmonyHost` wiring shape (interface in Abstractions,
concrete impl in `Stellar.Infrastructure`, constructed in a `Wiring.*.cs`, threaded into
`ConstructPluginServices`).

```csharp
namespace Stellar.Abstractions.Services;

/// <summary>
/// Injects audio into the game's OUTGOING team-voice stream (mixed with the live microphone).
/// Backed by a process-wide native DirectSound capture hook installed by the framework at boot.
/// Multiple sources — across all plugins — mix together into the one shared uplink.
/// </summary>
public interface IVoiceInjection
{
    /// <summary>
    /// True once the capture hook is installed AND the capture format is known (i.e. the mic is live,
    /// post-login). Audio assigned to a source before this is retained and becomes audible once true;
    /// it is simply not mixed yet. Poll or gate playback on this if you need the audio heard immediately.
    /// </summary>
    bool Available { get; }

    /// <summary>Hook / capture-format diagnostics for status UIs and logs.</summary>
    VoiceInjectionStatus Status { get; }

    /// <summary>
    /// Decode an audio file off-thread (WAV natively; MP3/WMA/M4A via Windows Media Foundation) into a
    /// reusable clip. Does NOT require the capture format to be known — the clip keeps its source PCM and is
    /// (re)resampled to the capture format on demand. Throws on a missing file or an unsupported/corrupt
    /// encoding. Media Foundation is absent on Windows "N" editions → non-WAV will fail there (WAV still works).
    /// </summary>
    Task<IVoiceClip> LoadFileAsync(string path);

    /// <summary>
    /// Build a clip from caller-supplied 16-bit interleaved PCM at the given sample rate and channel count.
    /// Copied immediately (the span need not outlive the call); resampled to the capture format on demand.
    /// Use for TTS or generated audio.
    /// </summary>
    IVoiceClip LoadPcm(ReadOnlySpan<short> pcm, int sampleRate, int channels);

    /// <summary>
    /// Create an independent playback voice in the mixer. Returns null if the mixer is at capacity.
    /// Dispose it to remove it from the mixer (sources a plugin leaves open are auto-disposed on plugin
    /// teardown). Typically a plugin keeps one source for looped background audio and/or uses
    /// <see cref="IVoiceSource.PlayOnce"/> for one-shots.
    /// </summary>
    IVoiceSource? CreateSource();

    /// <summary>
    /// Configure the GVoice uplink DSP, auto-applied while ANY source is playing and restored to the game's
    /// defaults when all sources stop. GVoice's noise-suppression / AGC attenuate injected (non-voice) audio,
    /// so disabling them preserves music fidelity; a higher Opus bitrate improves quality. The uplink encoder
    /// is process-global, so this is a single global policy — the last writer wins across plugins. A null
    /// argument leaves that setting unchanged. A disposed plugin's policy becomes inert once its sources stop.
    /// </summary>
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

    /// <summary>Convenience one-shot: assign the clip, clear Loop, and play from the start.</summary>
    void PlayOnce(IVoiceClip clip);
}

/// <summary>Capture-hook and format diagnostics.</summary>
public readonly record struct VoiceInjectionStatus(
    bool HookInstalled,   // IAT/vtable hook in place
    bool CaptureActive,   // a capture format has been observed (mic live)
    int  SampleRate,      // capture format (Hz); 0 until known — typically 16000 mono for GVoice
    int  Channels,
    bool ProbeMode,       // hook arrived after GVoice init → late-install probe path (mic opened briefly)
    string Detail);       // human-readable hook state (counters) for logs
```

### 3.1 Wiring checklist (framework side)
Per the recon map, adding `Voice` touches, in order:
1. `src\Stellar.Abstractions\Services\IPluginServices.cs` — add `IVoiceInjection Voice { get; }` (additive
   service prop = the analyzer's "safe" change).
2. `src\Stellar.Application\Services\PluginServices.cs` — add the auto-property, ctor param, assignment.
3. `src\Stellar.Application\Hosting\PerPluginServices.cs` — expose it **per-plugin** (a thin per-plugin façade
   over the shared mixer that tracks sources this plugin created and disposes them on teardown — mirrors
   `IHarmonyHost` auto-unpatch). Add to `PerPluginScope`.
4. `src\Stellar.Host\Wiring.PluginHost.cs::ConstructPluginServices` — pass the built instance in.
5. `FrameworkVersion.Value` → `2.23.0`, CHANGELOG `## [2.23.0]` (player bullets + `### Developer notes`),
   history line in `docs\contributing\api-changes.md`.

## 4. How to use it (plugin author guide)

The service is on `IPluginServices.Voice`. Typical patterns:

**Play a file into team voice (looped background audio):**
```csharp
var voice  = _services.Voice;
_source    = voice.CreateSource();                 // keep the handle; dispose on plugin teardown
var clip   = await voice.LoadFileAsync(@"C:\sounds\march.mp3");
_source!.Clip   = clip;
_source.Volume  = 1.0f;
_source.Loop    = true;
_source.Play();                                    // audible once voice.Available is true (post-login)
// optional: preserve music fidelity while it plays
voice.SetUplinkPolicy(disableNoiseSuppression: true, disableAgc: true, bitRateBps: 64000);
```

**Fire a one-shot (e.g. a callout sound), overlapping other audio:**
```csharp
var clip = await _services.Voice.LoadFileAsync(path);   // cache the clip; reuse it
var s    = _services.Voice.CreateSource();
s?.PlayOnce(clip);                                       // auto-idles at the end; dispose s when done, or reuse
```

**Inject generated / TTS audio:**
```csharp
short[] pcm = MyTts.Render("mechanic incoming");  // 16-bit interleaved
var clip    = _services.Voice.LoadPcm(pcm, sampleRate: 24000, channels: 1);
_source!.PlayOnce(clip);
```

**Gate on availability** — before login (or on a Windows "N" edition without Media Foundation for MP3),
`Available` is false and `LoadFileAsync` may throw for non-WAV. Handle both:
```csharp
if (!_services.Voice.Available) { /* retained; will be heard once the mic is live */ }
try { clip = await _services.Voice.LoadFileAsync(path); }
catch (Exception e) { _services.Log.Warning($"voice load failed: {e.Message}"); }
```

Notes for authors:
- Sources from all plugins mix together; keep your volume reasonable (additive-saturating against the mic).
- The uplink DSP policy is global and last-writer-wins. Only set it if you care about fidelity; it
  auto-restores when nothing is playing, so you do not have to unset it.
- You do not have to dispose sources on teardown (the framework does), but disposing a one-shot source you
  will not reuse frees a mixer slot sooner.

## 5. Architecture / internals (`Stellar.Infrastructure`)

Promote the plugin's hook essentially as-is; the hard native work is already validated. Changes are: (a) single
clip slot → **N-source mixer**, (b) install from boot instead of a plugin ctor, (c) clip holds source PCM with
lazy per-format conversion, (d) DSP apply/restore driven by "any source playing".

**5.1 Native capture hook (unchanged mechanism — see `Voice-Chat-GVoice.md`).**
IAT slot `GCloudVoice+0x6190D8` (DSOUND **ordinal #6** `DirectSoundCaptureCreate`, imported by ordinal → a
name-based IAT walker misses it) → hook `IDirectSoundCapture::CreateCaptureBuffer` (vtable +0x18) → hook the
capture buffer's `Lock` (+0x40). In the `Lock` postfix, additively saturating-mix our PCM into the returned
regions `p1/n1,p2/n2` **before** `CAudCapDs::FillBuffer` copies it out (Unlock entry is too late). Pointer
writes via `VirtualProtect` flip + 8-byte atomic store (`VoiceNative.WritePointer`). Guards: IAT RVA bounds vs
`SizeOfImage`; slot must still equal `dsound!DirectSoundCaptureCreate`; dsound vtables are process-shared, so
filter on the GVoice-origin buffer pointer (normal mode) — identity checks keep us off other DirectSound users.

**5.2 Boot-time install ordering (the key improvement).**
GVoice creates its capture device + buffer **once in `GCloudVoice_Init`** at login, so the IAT hook must be in
place first. In the plugin this forced a ctor-time force-load + a `ProbeInstall` fallback. In the framework,
install from `BootstrapPlugin.ApplyLifecyclePatches()` (runs in `OnHotUpdateReady`, at the title screen, before
login) — mirror `InstallInstrumentToneRelay`. The hook class lives in `Stellar.Infrastructure`
(e.g. `Game\VoiceCaptureHook.cs`), installed via a `Wiring.*.cs` call. Keep the `ProbeInstall` path for the
hot-reload case (framework attaches after GVoice already initialised) and expose it through `Status`/a log, not
the public API.

**5.3 Multi-source mixer (hot path, allocation-free).**
A fixed-capacity array (e.g. 16) of source slots; each slot carries volatile `clip`, `posSamples`, `gainQ8`,
`loop`, `playing`. The `Lock` hook iterates slots and, for each playing slot whose clip has a converted buffer
matching the current capture-format version, saturating-mixes it in (the existing `MixRegion` per slot, summing
into the same destination). **No allocation, no locks, no IL2CPP/game calls, nothing may throw** out of the
native-thread hook (the first reverse-P/Invoke on GVoice's capture thread makes CoreCLR set up a managed Thread
once — expected, harmless). Use `Interlocked`/`Volatile` only; CAS the position advance so a concurrent
Play/Stop/clip-swap wins over a stale advance (as the single-slot code already does).

**5.4 Clip conversion lifecycle.**
`IVoiceClip` holds the **source** PCM (`short[]` + rate + channels) plus a volatile **converted** buffer
(`short[]` at the current capture format) and the format-version it was converted for. Conversion
(`AudioDecoder.Convert`, linear-interp resample + channel map) runs **off-thread**. A framework maintainer —
either a dedicated background worker or a `Framework.Every(250ms)`/`Update` tick — re-converts every clip
referenced by a live source whenever `Status.SampleRate`/version changes (and on first format acquisition).
This generalises the single-clip `ReplaceClipKeepTime` poll. The hot path only ever reads a finished buffer.

**5.5 Uplink DSP (main-thread only).**
`GCloudVoice_invoke(cmd,p1,p2,*out)` (NS `0x21AC` / VAD `0x21AD` / AGC `0x21AE`) and
`GCloudVoice_SetBitRate(bps)`, resolved by `GetProcAddress` on the already-loaded `GCloudVoice.dll`. **Never
call these from the `Lock` hook.** Drive them from the maintainer tick: when the mixer transitions to
"any source playing" apply the policy; when it goes to "none playing" restore the game defaults (NS/AGC/VAD on,
bitrate 32000); re-apply every ~5 s while active (GVoice re-forces some settings in `OnMicOptBegin`). This is
the single-clip `VoiceDspSync` generalised from one clip to the mixer's aggregate play state.

**5.6 Teardown.**
The per-plugin façade tracks sources it created and disposes them on plugin unload (freeing mixer slots). The
native hook itself is **framework-lifetime** — restored only on framework teardown (`RestoreSlot` rewrites a
slot only if it still holds our pointer, so a chained hook is left intact). Restore stops all playback.

## 6. Versioning & feed

- Additive only → **minor bump to `2.23.0`** on `main`. `Stellar.Abstractions` ships as a NuGet package
  (nuget.org); plugins floor-reference it. A plugin that calls `Voice` bumps its `Stellar.Abstractions`
  `PackageReference` floor to `2.23.0`.
- Follow `docs\contributing\api-changes.md`: XML docs mandatory, CHANGELOG section, `FrameworkVersion` history
  line. Release cut by tagging `v2.23.0` (`release.yml` packs + publishes).
- **Ordering dependency for the Experiment migration (Phase 2):** Experiment can only consume `Voice` once
  `Stellar.Abstractions 2.23.0` is available to it. For local dev it references the locally built Abstractions;
  the public migration waits on the package publish. Keep Phase 1 and Phase 2 as separate commits/PRs.

## 7. Migration map (Experiment Voice Inject → API consumer, Phase 2)

Experiment keeps its **Voice Inject window** (a control/diagnostics UI) but the engine moves to the framework.

> ⚠️ **What's actually deletable depends on the branch — the shared files are the trap.** The migration was done
> on `enhance/mechanic-alerts`, where **Mechanic Callouts' local speaker playback (`MechAudioPlayer.cs`,
> `Plugin.MechanicAlerts.cs`) still decodes files via `AudioDecoder`**, and `AudioDecoder.MediaFoundation.cs` in
> turn depends on the **MF/COM helpers in `VoiceNative.cs`**. So on that branch only three files are injection-only
> and safe to delete; `AudioDecoder*.cs` and `VoiceNative.cs` must stay. (On a hypothetical main-based migration
> with no Mechanic Callouts, all six could go — but verify consumers each time, don't trust this list.)

- **Delete** (injection-only): `VoiceCaptureHook.cs`, `VoiceCaptureHook.Mix.cs`, `VoiceDsp.cs`.
- **Keep** `AudioDecoder.cs` + `AudioDecoder.MediaFoundation.cs` (used by Mechanic Callouts local playback) and
  `VoiceNative.cs` (its MF/COM helpers back the retained MP3 decoder; its DirectSound-only helpers become dead but
  compile standalone). **Also remove the now-dead `AudioDecoder.Convert(...)`/`Sample(...)`** — it returned the
  deleted `VoiceClip` and had no caller once the plugin stopped doing its own capture-format conversion (the
  framework owns that). Leaving it in is the compile break this migration actually hit.
- **Rewrite** `Plugin.VoiceInject.cs` / `Plugin.VoiceInject.Dsp.cs` to consume `_services.Voice`: one
  `IVoiceSource` for the loaded file; Load → `LoadFileAsync`; Play/Stop/Loop/Volume → source members; the DSP
  toggles + bitrate dropdown → one `SetUplinkPolicy(...)` call; status rows → `Status`. The deferred
  re-conversion poll, the probe button, and the native format plumbing all disappear (the framework owns them).
- Net: Experiment loses ~900 LoC of native plumbing, gains a handful of service calls. It becomes the reference
  consumer of `IVoiceInjection`.

## 8. Risks & gotchas

- **Native-thread safety is non-negotiable.** The `Lock` hook runs on GVoice's capture thread every ~20 ms:
  no alloc / lock / log / IL2CPP, whole body try/catch, always return the original HRESULT. Carry this verbatim
  from the plugin — it is the one thing that will crash the game if broken.
- **DLL-version brittleness.** The IAT RVA `0x6190D8` and vtable slot offsets are pinned to
  `GCloudVoice.dll 4.3.0.253999397`. The guards (RVA bounds, slot-equals-`DirectSoundCaptureCreate`) fail safe
  (no patch, logged) if the DLL changes — but a new game build may need re-RE. `Status.Detail` must surface this.
- **Probe fallback opens the mic briefly.** Only used on late attach (hot-reload); boot install avoids it in
  the normal path. Keep it, document the side effect.
- **Process-global DSP, last-writer-wins.** Acceptable because it auto-restores when nothing plays. Do not try
  to make it per-plugin stateful — there is no getter to read the live GVoice 3A state back, so reconciliation
  across plugins is infeasible. Document the single-policy contract.
- **Mixer capacity.** Fixed array; `CreateSource` returns null at capacity. Pick a sane cap (16) and log when hit.
- **Media Foundation absent on Windows "N".** MP3/WMA/M4A `LoadFileAsync` throws there; WAV always works. Say so
  in the XML doc (done above) so plugin authors handle it.
- **Live push-streaming deferred.** If a future consumer needs real-time generated audio, add a streaming source
  (ring buffer fed by the plugin, drained by the hook) as a second `IVoiceSource` flavour — additive, no change
  to what's here.

## 9. Execution phases

1. **Phase 1 — framework (this repo, branch off `main`).** Add the Abstractions surface (§3) with XML docs;
   port the hook/decoder/native/DSP into `Stellar.Infrastructure` as the N-source mixer (§5); wire into
   `PluginServices`/`PerPluginServices`/`Wiring.PluginHost`; install from `BootstrapPlugin`; bump to `2.23.0`
   + CHANGELOG + api-changes history. Compile-PASS. **In-game validation owed** (play a file into team voice,
   confirm teammates hear it mixed with the mic) before it is "done".
2. **Phase 2 — Experiment migration (plugin repo).** After `2.23.0` Abstractions is available: delete the
   native files, rewrite the Voice Inject window onto `IVoiceInjection`, floor-bump the package ref. Separate
   commit/PR. In-game re-validate the window end-to-end.
3. **Docs.** Update KB `Voice-Chat-GVoice.md` with a pointer to the framework service (and a one-line hook in
   `Knowledge Base\INDEX.md`); add a short "Voice injection" section to ModSystem `docs\plugin-development.md`.
