using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Detects whether the game's loading screen (<c>loading_window</c> under <c>UILayerSystemTip</c>) is
/// currently up, so the Host can drive the <see cref="Stellar.Abstractions.Domain.GameUIState.Loading"/> bit
/// reliably. This is the SOLE owner of that bit — <see cref="PandaMenuStateProbe"/> no longer sets it.
///
/// <para><b>Why a separate un-gated probe.</b> The loading screen is up precisely while
/// <c>IClientState.IsWorldActive</c> is <c>false</c> (the zone-load / world-connect handshake). The
/// menu-state probe runs inside the <c>IsWorldActive</c>-gated <c>_framework.Tick</c>, so it is frozen for
/// the whole load and its cached <c>Loading</c> bit goes stale (never set). This probe is ticked from the
/// Host's UN-gated per-tick path (<c>RunGlobalRateWork</c>, above the <c>if (IsWorldActive) _framework.Tick()</c>
/// line) so the bit updates every phase. Reading a GameObject's active-state is a pure UI read (no
/// game-state / network mutation), safe every phase — exactly like <see cref="PandaLoginViewProbe"/> and the
/// draw services.</para>
///
/// <para><b>Cost.</b> Mirrors the menu-state / login-view probes: the <c>zuiroot</c> transform is resolved
/// once (re-resolved only if it dies on a scene change), then a cheap child scan of <c>UILayerSystemTip</c>.
/// Self-throttled to ~10 Hz. The <c>loading_window</c> prefix scan (not any-child) is required because
/// <c>UILayerSystemTip</c> also hosts Permanent views active during normal play (tips_broadcast / sys_dialog).</para>
/// </summary>
internal sealed class PandaLoadingScreenProbe
{
    private const string RootName            = "zuiroot";
    private const string SystemTipLayerName  = "UILayerSystemTip";
    private const string LoadingWindowPrefix = "loading_window";   // matches loading_window / loading_window_pc(Clone)

    // ~10 Hz at 60 fps — a loading screen lasts seconds; per-frame latency is unnecessary.
    private const int CheckIntervalTicks = 6;

    private int _ticksUntilCheck;
    private Transform? _zuiroot;   // cached persistent UI root; Unity '== null' detects scene-change destruction

    /// <summary>True when the game's <c>loading_window</c> is active in the hierarchy. Cached between the
    /// ~10 Hz checks; read every tick regardless.</summary>
    public bool IsLoadingScreenActive { get; private set; }

    public void Tick()
    {
        if (--_ticksUntilCheck > 0) return;
        _ticksUntilCheck = CheckIntervalTicks;

        // (Re)resolve the root only when missing/destroyed — the only global scan, ~once per scene.
        if (_zuiroot == null)
        {
            var root = GameObject.Find(RootName);
            _zuiroot = root != null ? root.transform : null;
            if (_zuiroot == null) { IsLoadingScreenActive = false; return; }
        }

        IsLoadingScreenActive = LoadingScreenActive(_zuiroot);
    }

    // Scan UILayerSystemTip children for an active one whose name starts with "loading_window" (handles the
    // Unity "(Clone)" suffix). Transform.Find walks the relative path only (cheap) and sees inactive objects.
    private static bool LoadingScreenActive(Transform root)
    {
        var layer = root.Find(SystemTipLayerName);
        if (layer == null) return false;
        for (var i = 0; i < layer.childCount; i++)
        {
            var child = layer.GetChild(i);
            if (child.gameObject.activeInHierarchy && child.name.StartsWith(LoadingWindowPrefix))
                return true;
        }
        return false;
    }
}
