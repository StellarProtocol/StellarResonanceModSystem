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
    private readonly IPluginLog _log;
    private readonly ITheme _theme;
    public PandaUGuiAdapter(IPluginLog log, ITheme theme) { _log = log; _theme = theme; }

    /// <summary>Destroys the rail-button icon textures on framework teardown (no leak on soft reload).</summary>
    public void Dispose() => _iconCache.Dispose();

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

    private static Transform? ResolveParent(NativeUiAnchor anchor)
    {
        if (!UGuiAnchorAllowlist.TryGet(anchor, out var entry)) return null;
        var go = GameObject.Find(entry.InsertionParentPath);
        return go != null ? go.transform : null;
    }

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
