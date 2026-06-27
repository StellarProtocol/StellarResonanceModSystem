using System.Collections.Generic;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.UI;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private PerfPrefs? _perfPrefs;
    private NativeUiService? _nativeUi;
    private bool _nativeUiReconLogged;

    private void BuildNativeUiServices(BepInExPluginLog log)
    {
        // Performance settings (Settings → Performance). Construction seeds PerfControls from the
        // persisted "perf" section (unless a dev env/flags override is present); the host tick then
        // reconciles the live framework tick rate + V-Sync uncap to PerfControls each tick.
        _perfPrefs = new PerfPrefs(_pluginConfigService!.GetSection("perf"));
        _perfPrefs.OnGlobalRateChanged = hz => _scheduler?.SetGlobalRate(hz);

        // Native UI: adapter + service. Allowlist projected from Infrastructure
        // to Application's descriptor type.
        var gameUiSection = _pluginConfigService!.GetSection("gameui");
        var descriptors = new List<NativeUiEntryDescriptor>();
        foreach (var entry in NativeUiAllowlist.V1Targets)
            descriptors.Add(new NativeUiEntryDescriptor(entry.Id, entry.DisplayName, entry.Path)
                { SafeToHide = entry.SafeToHide, RectChild = entry.RectChild });
        _nativeUi = new NativeUiService(new PandaHudAdapter(log), gameUiSection,
                                        () => _layoutStorage!.ActiveSlot, log, descriptors);
    }

    /// <summary>
    /// One-shot Canvas-hierarchy dump for the Task 17 allowlist recon. Gated
    /// on STELLAR_NATIVEUI_RECON=1 so it doesn't fire on normal runs. Invoked
    /// from <c>OnEnterScene</c> when scene 7/8 (in-world) loads.
    /// </summary>
    internal void TryAutoReconNativeUi(BepInExPluginLog log)
    {
        if (_nativeUiReconLogged) return;
        var env = System.Environment.GetEnvironmentVariable("STELLAR_NATIVEUI_RECON");
        if (string.IsNullOrEmpty(env) || env == "0") return;
        _nativeUiReconLogged = true;

        try
        {
            log.Info("[NativeUi/Recon] auto-recon (STELLAR_NATIVEUI_RECON=1) — dumping canvas hierarchy");
            Stellar.Infrastructure.Game.NativeUiReconWalker.Walk(log.Info);
            Stellar.Infrastructure.Hooks.InputPathRecon.DumpOnce(log.Info);
            Stellar.Infrastructure.Hooks.MenuStateRecon.DumpActiveLayers(log.Info);
            log.Info("[NativeUi/Recon] auto-recon complete");
        }
        catch (System.Exception ex)
        {
            log.Warning($"[NativeUi/Recon] auto-recon threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
