using System;
using System.IO;
using BepInEx;
using Stellar.Infrastructure.Configuration;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Rendering;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── ReShade (Wiring.ReShade.cs) — IReShade + the bridge the frame grabber draws through ──
    private const int ReShadeRefreshWarnCap = 3;
    private ReShadeBridge? _reShadeBridge;     // shared: the service polls it, the frame grabber renders through it
    private ReShadeService? _reShadeService;   // IPluginServices.ReShade; refreshed once per global-rate tick
    private BepInExPluginLog? _reShadeLog;
    private int _reShadeRefreshFailures;

    /// <summary>
    /// Constructs the bridge wrapper and the <c>IReShade</c> service. Runs in <c>Load()</c> before
    /// <see cref="WirePhotoStudio"/> (the frame grabber and the capture service take them). Nothing is loaded here: the
    /// bridge only LOOKS UP the add-on module ReShade loaded, so without ReShade (or on a vanilla launch) the service
    /// stays unavailable and every capture is exactly the pre-ReShade one.
    /// </summary>
    private void WireReShade(BepInExPluginLog log)
    {
        _reShadeLog = log;
        _reShadeBridge = new ReShadeBridge(log);
        var effectFiles = new PhysicalEffectFileSystem();
        _reShadeService = new ReShadeService(_reShadeBridge, new EffectDepthIndex(effectFiles), new EffectSizeLockIndex(effectFiles),
            new EffectTemporalIndex(effectFiles), log);
    }

    /// <summary>The absolute config file the bridge's isolated capture writes (with its preset copy beside it); the
    /// bridge creates the folder. Null (no isolated capture) if the game root cannot be resolved.</summary>
    private string? IsolatedCaptureConfigPath()
    {
        try
        {
            return Path.GetFullPath(Path.Combine(Paths.GameRootPath, FrameworkPaths.ReShadeCaptureSubdir,
                FrameworkPaths.ReShadeCaptureConfigFile));
        }
        catch (Exception ex)
        {
            _reShadeLog?.Warning($"[ReShade] isolated capture off: no config path ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>Called from <c>RunGlobalRateWork</c> (un-gated: ReShade is usable at the title screen too). The service
    /// is written not to throw; this guard makes "never throws into the game" hold even if it does.</summary>
    private void TickReShade()
    {
        if (_reShadeService is null) return;
        try
        {
            _reShadeService.Refresh();
        }
        catch (Exception ex)
        {
            if (++_reShadeRefreshFailures > ReShadeRefreshWarnCap) return;
            var tail = _reShadeRefreshFailures == ReShadeRefreshWarnCap ? " (further failures not logged)" : "";
            _reShadeLog?.Warning($"[ReShade] refresh failed: {ex.GetType().Name}: {ex.Message}{tail}");
        }
    }
}
