using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Voice injection (Wiring.Voice.cs) — IPluginServices.Voice: the shared multi-source uplink mixer ──
    private VoiceInjectionService? _voiceService;

    /// <summary>
    /// Builds the shared voice-injection service and subscribes its maintainer tick. Runs in <c>Load()</c> before
    /// <see cref="WireGameEventsAndPluginHost"/> (the plugin-services aggregator takes <c>_voiceService</c>). Nothing
    /// native is touched here — the capture hook is installed later, from <see cref="InstallVoiceCaptureHook"/> at the
    /// title screen. The maintainer rides the framework <c>Update</c> tick (like <c>entityVitals.Tick</c>): it
    /// re-converts live clips to the capture format off-thread, applies/restores the uplink DSP from the mixer's
    /// aggregate play state, and runs the hot-reload probe fallback — all of which matter only in-world.
    /// </summary>
    private void WireVoiceInjection(BepInExPluginLog log)
    {
        _voiceService = new VoiceInjectionService(log.Info);
        _framework!.Update += _ => _voiceService!.Tick();
    }

    /// <summary>
    /// Installs the process-global native DirectSound capture hook on <c>GCloudVoice.dll</c>. Called from
    /// <see cref="ApplyLifecyclePatches"/> (runs in <c>OnHotUpdateReady</c>, title screen, BEFORE login's
    /// <c>GCloudVoice_Init</c> creates the capture buffer) — mirrors <see cref="InstallInstrumentToneRelay"/>. The
    /// hook + all native/unsafe code live in <c>Stellar.Infrastructure</c>; Host only calls the installer and records
    /// the result on the service so the maintainer can run the hot-reload probe fallback when needed.
    /// </summary>
    private void InstallVoiceCaptureHook(BepInExPluginLog log)
    {
        var result = VoiceCaptureHook.Install();
        _voiceService?.NotifyInstalled(result);
    }
}
