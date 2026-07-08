using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>WindowBuilder GlassMenu chrome — the frosted-glass frame (reused baked 9-slice sprite),
/// title bar (bold title + ✕ close + accent bottom divider), and the padded content container the
/// element tree builds into. Title bar is the drag handle basis (drag/persist wired by WindowService in
/// Plan 4). Measurement contract: corner radius 14 (baked), body padding 12, title bar padding 9 v / 13 h.
/// Plan 2 swaps the baked sprite for the full per-preset frosted bake + the other four chrome styles.</summary>
internal sealed partial class WindowBuilder
{
    private const float TitleBarHeight = 32f;

    /// <summary>Dispatch the chrome on the window's style. GlassMenu is the redesigned frosted frame;
    /// Borderless is frameless; Tracker/Party/PillStatus are overlay chromes (sibling partial).</summary>
    private (RectTransform root, Transform content) BuildChrome(WindowRegistration reg, Transform parent, WindowToken token)
    {
        var result = reg.Spec.Style switch
        {
            WindowPanelStyle.Borderless => BuildBorderless(reg.Spec, parent),
            WindowPanelStyle.Tracker    => BuildTrackerChrome(reg.Spec, parent),
            WindowPanelStyle.Party      => BuildPartyChrome(reg.Spec, parent),
            WindowPanelStyle.PillStatus => BuildPillChrome(reg.Spec, parent),
            _                            => BuildGlassChrome(reg, parent, token),   // GlassMenu + default
        };
        // GlassMenu/default registers its own drag inside BuildGlassChrome. The other styles have no title bar,
        // so register whole-frame drag here when Draggable. Overlay/status chromes (Tracker/Party/PillStatus)
        // are edit-only (move only in layout edit-mode, not during play — e.g. AutoNav); Borderless is the
        // launcher → free drag.
        // Borderless optional black background — on the root's existing click-blocker Image so it fills the
        // full window rect and expands when the user resizes height (no separate child GO needed).
        if (reg.Spec.Style == WindowPanelStyle.Borderless && reg.Spec.BackgroundOpacity is { } bgOp
            && result.root.GetComponent<UnityEngine.UI.Image>() is { } blocker)
        {
            blocker.color = new UnityEngine.Color(0f, 0f, 0f, bgOp());
            token.FrameOpacities.Add(new FrameOpacityBinding { Img = blocker, Opacity = bgOp });
        }
        if (reg.Spec.Draggable && reg.Spec.Style != WindowPanelStyle.GlassMenu)
        {
            // Drag mode is explicit per window (EditModeDragOnly), NOT inferred from chrome style — so a
            // Party-chromed popup dialog (CombatMeter History) free-drags while a PillStatus HUD overlay
            // (AutoNav) is edit-only. Overlay HUDs opt into edit-only by setting the flag.
            _registerWindowDrag?.Invoke(result.root, result.root, reg.Spec.EditModeDragOnly);
        }
        // Overlay chromes (Tracker/Party/PillStatus) get a top-right ✕ when Closable (GlassMenu draws its own
        // in the title bar). So a Party-chromed dialog like CombatMeter History can be closed.
        if (reg.Spec.Closable && reg.Spec.Style is WindowPanelStyle.Tracker or WindowPanelStyle.Party or WindowPanelStyle.PillStatus)
            BuildOverlayCloseButton(result.root, token, reg.OnClose);
        if (reg.Spec.Resizable) MakeResizable(result.root, reg.Spec);
        return result;
    }

    // Switch a window root from content-height-fit to a FIXED grip-resizable size + add the ↘ corner grip.
    // The vertical ContentSizeFitter is disabled so the body's flexible ScrollElement fills the fixed height;
    // the grip is registered with the interaction ticker (which clamps to the spec's min/max on drag).
    private void MakeResizable(RectTransform root, WindowSpec spec)
    {
        // Both fits Unconstrained so the grip-driven sizeDelta sticks (Borderless content-fits width by default,
        // which would otherwise override the resized width).
        if (root.GetComponent<ContentSizeFitter>() is { } fit)
        {
            fit.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        }
        var w = spec.DefaultRect.Width > 0 ? spec.DefaultRect.Width : 360f;
        var h = spec.DefaultRect.Height > 0 ? spec.DefaultRect.Height : 320f;
        root.sizeDelta = new Vector2(w, h);
        // Let the body sections fill the now-fixed height (a flexible ScrollElement absorbs the slack).
        if (root.GetComponent<VerticalLayoutGroup>() is { } vlg) vlg.childForceExpandHeight = false;

        var grip = UGuiPrimitives.NewChild("ResizeGrip", root);
        grip.AddComponent<LayoutElement>().ignoreLayout = true;
        var grt = grip.GetComponent<RectTransform>();
        grt.anchorMin = grt.anchorMax = grt.pivot = new Vector2(1f, 0f);   // bottom-right corner
        grt.sizeDelta = new Vector2(16f, 16f); grt.anchoredPosition = new Vector2(-1f, 1f);
        // Transparent hit area for the drag; the visible affordance is the ↘ dotted triangle below (matches the
        // IMGUI DrawResizeGrip: 3 rows of 2-px dots, 3/2/1, in the corner). A round sprite read as a blob.
        var hit = grip.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f); hit.raycastTarget = true;
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 3 - r; c++)
        {
            var dot = UGuiPrimitives.NewChild("Dot", grip.transform);
            var drt = dot.GetComponent<RectTransform>();
            drt.anchorMin = drt.anchorMax = drt.pivot = new Vector2(1f, 0f);
            drt.sizeDelta = new Vector2(2f, 2f);
            drt.anchoredPosition = new Vector2(-(c * 4f), r * 4f);
            var di = dot.AddComponent<Image>(); di.color = new Color(1f, 1f, 1f, 0.34f); di.raycastTarget = false;
        }
        RegisterResize?.Invoke(grt, root, new Vector2(spec.MinWidth, spec.MinHeight), new Vector2(spec.MaxWidth, spec.MaxHeight));
    }
    // Overlay chromes (Tracker/Party/PillStatus) implemented in WindowBuilder.Chrome.Overlay.cs.

    // Frameless container — no bg/border/title. Content floats on the game; optional drag (Plan 4).
    private (RectTransform root, Transform content) BuildBorderless(WindowSpec spec, Transform parent)
    {
        var root = UGuiPrimitives.NewRect(spec.Id, parent);
        var rootLe = root.gameObject.AddComponent<LayoutElement>();
        rootLe.preferredWidth = spec.DefaultRect.Width > 0 ? spec.DefaultRect.Width : 300f;
        var fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        // Invisible raycast blocker so clicks/right-clicks over the window don't fall through to the game.
        var blocker = root.gameObject.AddComponent<Image>();
        blocker.color = new Color(0f, 0f, 0f, 0f);
        blocker.raycastTarget = true;
        var vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(12, 12, 12, 12);
        vlg.spacing = SectionGap;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperLeft;
        return (root, root.transform);   // root IS the content host (no separate container)
    }

    private (RectTransform root, Transform content) BuildGlassChrome(WindowRegistration reg, Transform parent, WindowToken token)
    {
        var spec = reg.Spec;
        var root = UGuiPrimitives.NewRect(spec.Id, parent);
        ConfigureGlassRoot(root, spec, token);

        // Drag mode is the per-window EditModeDragOnly flag for EVERY chrome (default false = free-drag any time;
        // true = pinned, moves only in Shift+` edit mode) — so any chrome can be either a free dialog or a HUD.
        if (spec.ShowTitleBar)
        {
            var titleBar = BuildTitleBar(reg, root.transform, token);
            if (spec.Draggable) _registerWindowDrag?.Invoke(titleBar.GetComponent<RectTransform>(), root, spec.EditModeDragOnly);
        }
        else if (spec.Draggable)
        {
            // No title bar → the whole frame is the drag handle (the launcher self-draws its header in the body).
            _registerWindowDrag?.Invoke(root, root, spec.EditModeDragOnly);
        }

        var content = UGuiPrimitives.NewChild("Content", root.transform);
        var clg = content.AddComponent<VerticalLayoutGroup>();
        clg.padding = new RectOffset(12, 12, 12, 12);   // contract: body padding 12
        clg.spacing = SectionGap;                        // 12 between top-level sections
        clg.childControlWidth = true; clg.childControlHeight = true;
        clg.childForceExpandWidth = true; clg.childForceExpandHeight = false;   // sections fill the window width
        clg.childAlignment = TextAnchor.UpperLeft;
        return (root, content.transform);
    }

    // Root sizing (fixed or content-sized width) + the frosted frame Image + live opacity.
    private void ConfigureGlassRoot(RectTransform root, WindowSpec spec, WindowToken token)
    {
        var fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        if (spec.AutoSizeWidth)
        {
            // Content-size the width to the body (the launcher's fixed-width tiles → no wrapping-text overflow).
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
        else
        {
            // FIXED width (from the spec rect) so Text wraps + the scroll mask never clips (the in-world clip bug).
            var width = spec.DefaultRect.Width > 0 ? spec.DefaultRect.Width : 360f;
            root.sizeDelta = new Vector2(width, root.sizeDelta.y);
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        }
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 0f; vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;

        var frame = root.gameObject.AddComponent<Image>();
        frame.sprite = _assets.FrameBg; frame.type = Image.Type.Sliced; frame.raycastTarget = true;
        token.ReskinActions.Add(() => { if (frame != null) frame.sprite = _assets.FrameBg; });
        if (_assets.OpacityProvider is { } op)
        {
            var c0 = frame.color; frame.color = new Color(c0.r, c0.g, c0.b, op());
            token.FrameOpacities.Add(new FrameOpacityBinding { Img = frame, Opacity = op });
        }
    }

    private GameObject BuildTitleBar(WindowRegistration reg, Transform parent, WindowToken token)
    {
        var spec = reg.Spec;
        var bar = UGuiPrimitives.NewChild("TitleBar", parent);
        var bg = bar.AddComponent<Image>();
        // Top-rounded sprite at the FRAME radius so the tint matches the frame's rounded top corners
        // exactly — no square-corner (or under-rounded) leak past the curve.
        bg.sprite = _assets.TitleBg; bg.type = Image.Type.Sliced;
        bg.color = new Color(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.14f);
        bg.raycastTarget = true;   // drag-handle basis (drag wired in Plan 4)
        token.ReskinActions.Add(() => { if (bg != null) { bg.sprite = _assets.TitleBg; bg.color = new Color(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.14f); } });
        var le = bar.AddComponent<LayoutElement>(); le.minHeight = TitleBarHeight; le.preferredHeight = TitleBarHeight;

        var hlg = bar.AddComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(13, 13, 9, 9);     // contract: 13 h / 9 v
        hlg.spacing = 6f; hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;

        // Title content: a caller-supplied leading accessory (e.g. logo + wordmark) OR the plain title text.
        if (reg.TitleLeading != null) BuildElement(reg.TitleLeading, bar.transform, token);
        else BuildTitleText(spec, bar, token);

        var spacer = UGuiPrimitives.NewChild("Sp", bar.transform);
        spacer.AddComponent<LayoutElement>().flexibleWidth = 1f;

        // Trailing accessory (e.g. mode/rotate toggles) sits right-aligned, before the ✕.
        if (reg.TitleTrailing != null) BuildElement(reg.TitleTrailing, bar.transform, token);

        if (spec.Closable) BuildCloseButton(bar, token, reg.OnClose);
        BuildTitleDivider(bar, token);
        return bar;
    }

    private void BuildTitleText(WindowSpec spec, GameObject bar, WindowToken token)
    {
        var titleGo = UGuiPrimitives.NewChild("Title", bar.transform);
        var title = titleGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(title, Scaled(13), TextAnchor.MiddleLeft, bold: true);
        title.color = _assets.MenuText; title.text = spec.Title; title.raycastTarget = false;
        RegisterTextReskin(token, title, 13);
    }

    // 1 px accent divider pinned to the title bar's bottom edge (ignore-layout overlay).
    private void BuildTitleDivider(GameObject bar, WindowToken token)
    {
        var div = UGuiPrimitives.NewChild("TitleDivider", bar.transform);
        div.AddComponent<LayoutElement>().ignoreLayout = true;
        var drt = div.GetComponent<RectTransform>();
        drt.anchorMin = new Vector2(0f, 0f); drt.anchorMax = new Vector2(1f, 0f);
        drt.sizeDelta = new Vector2(0f, 1f); drt.anchoredPosition = Vector2.zero;
        var dimg = div.AddComponent<Image>();
        dimg.color = new Color(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.55f);
        dimg.raycastTarget = false;
        token.ReskinActions.Add(() => { if (dimg != null) dimg.color = new Color(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.55f); });
    }

    // Clickable ✕. Prefer the registration's OnClose (drives IWindowControl.SetVisible(false) so IsShown stays
    // in sync → a rail/hotkey toggle reopens on the first press); fall back to hiding the GameObject directly
    // (token.Root is assigned right after BuildChrome returns, so the closure reads it lazily).
    private void BuildCloseButton(GameObject bar, WindowToken token, System.Action? onClose = null)
    {
        var x = UGuiPrimitives.NewChild("Close", bar.transform);
        var xt = x.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(xt, Scaled(14), TextAnchor.MiddleCenter, bold: true);
        xt.color = _assets.MenuMuted; xt.text = "✕"; xt.raycastTarget = true;
        RegisterTextReskin(token, xt, 14, muted: true);
        var closeBtn = x.AddComponent<UnityEngine.UI.Button>(); closeBtn.targetGraphic = xt;
        closeBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
        {
            if (onClose != null) onClose();
            else if (token.Root != null) token.Root.SetActive(false);
        }));
    }

    // Corner ✕ for the overlay chromes (no title bar to host it) — anchored at the window's top-right,
    // ignore-layout so it floats over the banner/content. Same onClose contract as BuildCloseButton.
    private void BuildOverlayCloseButton(RectTransform root, WindowToken token, System.Action? onClose)
    {
        var x = UGuiPrimitives.NewChild("Close", root);
        x.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = x.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);   // top-right corner
        rt.sizeDelta = new Vector2(18f, 18f); rt.anchoredPosition = new Vector2(-6f, -4f);
        var xt = x.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(xt, Scaled(14), TextAnchor.MiddleCenter, bold: true);
        xt.color = _assets.MenuText; xt.text = "✕"; xt.raycastTarget = true;
        RegisterTextReskin(token, xt, 14);
        var closeBtn = x.AddComponent<UnityEngine.UI.Button>(); closeBtn.targetGraphic = xt;
        closeBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
        {
            if (onClose != null) onClose();
            else if (token.Root != null) token.Root.SetActive(false);
        }));
    }
}
