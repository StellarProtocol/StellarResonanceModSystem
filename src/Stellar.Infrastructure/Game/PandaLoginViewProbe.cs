using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Detects whether the game's login view (<c>login_main</c>) is currently up, so the Host can latch the
/// <see cref="Stellar.Abstractions.Domain.GamePhase.Startup"/> → <see cref="Stellar.Abstractions.Domain.GamePhase.TitleScreen"/>
/// transition. The confirmed runtime hierarchy (release_3.7) is
/// <c>zuiroot/UILayerMain/login_main(Clone)</c> — see Knowledge Base/Login-Screen-UI-Injection.md and
/// Login-Flow.md.
///
/// <para><b>Where this runs.</b> Unlike <see cref="PandaMenuStateProbe"/> (ticked inside the
/// <c>IsWorldActive</c>-gated <c>_framework.Tick</c>), this probe is ticked from the Host's UN-gated per-tick
/// path — it must run during <see cref="Stellar.Abstractions.Domain.GamePhase.Startup"/>, when
/// <c>IsWorldActive</c> is false, or the transition would never fire. Reading a GameObject's active-state is a
/// pure UI read (no game-state / network mutation), so it is safe to run every phase — exactly like the draw
/// services.</para>
///
/// <para><b>Cost.</b> Mirrors the menu-state probe: the <c>zuiroot</c> transform is resolved once (re-resolved
/// only if it dies on a scene change) and the login-view lookup is a cheap relative <see cref="Transform.Find"/>
/// + child scan under that cached root. Self-throttled to ~10 Hz.</para>
/// </summary>
internal sealed class PandaLoginViewProbe
{
    private const string RootName        = "zuiroot";
    private const string MainLayerName   = "UILayerMain";
    private const string LoginViewSubstr = "login_main";   // matches login_main(Clone); name-CONTAINS per KB doc

    // ~10 Hz at 60 fps — the login screen appears/disappears slowly; per-frame latency is unnecessary.
    private const int CheckIntervalTicks = 6;

    private int _ticksUntilCheck;
    private Transform? _zuiroot;   // cached persistent UI root; Unity '== null' detects scene-change destruction

    /// <summary>True when the game's <c>login_main</c> view is active in the hierarchy. Cached between the
    /// ~10 Hz checks; read every tick regardless.</summary>
    public bool IsLoginViewActive { get; private set; }

    public void Tick()
    {
        if (--_ticksUntilCheck > 0) return;
        _ticksUntilCheck = CheckIntervalTicks;

        // (Re)resolve the root only when missing/destroyed — the only global scan, ~once per scene.
        if (_zuiroot == null)
        {
            var root = GameObject.Find(RootName);
            _zuiroot = root != null ? root.transform : null;
            if (_zuiroot == null) { IsLoginViewActive = false; return; }
        }

        IsLoginViewActive = LoginViewActive(_zuiroot);
    }

    // Scan the UILayerMain children for an active one whose name contains "login_main" (handles the
    // Unity "(Clone)" suffix). Transform.Find walks the relative path only (cheap) and sees inactive
    // objects — no global scan.
    private static bool LoginViewActive(Transform root)
    {
        var layer = root.Find(MainLayerName);
        if (layer == null) return false;
        for (var i = 0; i < layer.childCount; i++)
        {
            var child = layer.GetChild(i);
            if (child.gameObject.activeInHierarchy && child.name.Contains(LoginViewSubstr))
                return true;
        }
        return false;
    }
}
