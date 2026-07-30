using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// IUGuiCanvasAdapter implementation. Resolves anchor containers by path,
/// builds native-styled uGUI under them (clone for buttons, themed-from-scratch
/// for indicators/panels), and tracks the created GameObjects for liveness +
/// teardown. Construction lives in the .Build partial.
/// </summary>
internal sealed partial class PandaUGuiAdapter : IUGuiCanvasAdapter, System.IDisposable
{
    private const string ZuiRootName = "zuiroot";

    private readonly IPluginLog _log;
    private readonly ITheme _theme;

    // Cached persistent UI root — same bug class + fix as PandaProfileCardActionInjector.FindCardRoot:
    // the naive ResolveParent ran a path-form GameObject.Find (a FULL scene-hierarchy scan whose cost
    // grows with scene population, ~30 ms/hit in a dense dungeon) on the 5 Hz injection probe, forever
    // while the target menu was closed — the proven P0 frametime-spike source (A/B 2026-07-25).
    private Transform? _zuiroot;

    public PandaUGuiAdapter(IPluginLog log, ITheme theme) { _log = log; _theme = theme; }

    /// <summary>Destroys the rail-button icon textures + login-circle texture on framework teardown (no leak on soft reload).</summary>
    public void Dispose() { _iconCache.Dispose(); DestroyCircleTex(); }

    public bool IsAnchorAvailable(NativeUiAnchor anchor) => ResolveParent(anchor) != null;

    public object? Inject(NativeUiElementSpec spec)
    {
        var parent = ResolveParent(spec.Anchor);
        if (parent == null) return null;
        var go = spec switch
        {
            MenuButtonSpec b => BuildButton(spec.Anchor, b),     // finds its own clone target
            IndicatorSpec i  => BuildIndicator(parent, i),
            PanelSpec p      => BuildPanel(parent, p),
            _                => null,
        };
        if (go == null) { _log.Warning($"[uGUI] could not build {spec.GetType().Name} at {spec.Anchor}"); return null; }
        return new ElementRef(go, spec);
    }

    public bool IsAlive(object? elementRef) => elementRef is ElementRef e && e.Go != null;

    public void ApplyContent(object? elementRef, NativeUiElementSpec spec)
    {
        if (elementRef is not ElementRef e || e.Go == null) return;
        ApplyDynamic(e, spec); // .Build partial: refresh Indicator/Panel text + bars
    }

    public void Destroy(object? elementRef)
    {
        if (elementRef is ElementRef e && e.Go != null) UnityEngine.Object.Destroy(e.Go);
    }

    // Resolve the anchor container via the cached zuiroot + a cheap relative Transform.Find. The
    // activeInHierarchy guard preserves the old GameObject.Find active-only contract (a closed menu's
    // window is inactive/destroyed → anchor unavailable, exactly as before). zuiroot re-resolves only
    // after a scene change kills it; the root lookup is a bare-name Find (no path walk).
    private Transform? ResolveParent(NativeUiAnchor anchor)
    {
        if (!UGuiAnchorAllowlist.TryGet(anchor, out var entry)) return null;
        if (_zuiroot == null)
        {
            var root = GameObject.Find(ZuiRootName);
            _zuiroot = root != null ? root.transform : null;
            if (_zuiroot == null) return null;
        }
        // Login sidebar: resolve the login view by NAME-CONTAINS "login_main" (the exact runtime name isn't
        // guaranteed — _pc / (Clone) variants — so an exact Transform.Find can miss). See .LoginButton partial.
        if (anchor == NativeUiAnchor.LoginSidebar) return ResolveLoginView(_zuiroot);
        var rel = ToZuiRelativePath(entry.InsertionParentPath);
        if (rel == null)
        {
            // Non-zuiroot allowlist path (none exist today): keep the legacy resolve so a future
            // entry degrades to correct-but-slow instead of silently never injecting.
            var legacy = UnityEngine.GameObject.Find(entry.InsertionParentPath);
            return legacy != null ? legacy.transform : null;
        }
        var t = _zuiroot.Find(rel);
        return t != null && t.gameObject.activeInHierarchy ? t : null;
    }

    private static string? ToZuiRelativePath(string path)
        => path.StartsWith(ZuiRootName + "/", System.StringComparison.Ordinal)
            ? path.Substring(ZuiRootName.Length + 1)
            : null;

    // Opaque ref handed back to Application; only this adapter reads it.
    private sealed class ElementRef
    {
        public ElementRef(GameObject go, NativeUiElementSpec spec) { Go = go; Spec = spec; }
        public GameObject Go;
        public NativeUiElementSpec Spec;
        // Row/content Text components, resolved once on first refresh so the
        // per-tick ApplyDynamic doesn't re-run GetComponentsInChildren each time.
        public Text[]? Texts;
    }
}
