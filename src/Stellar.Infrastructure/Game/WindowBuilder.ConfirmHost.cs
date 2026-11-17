using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

// ConfirmHostElement (framework 2.33.0): a window-scoped modal confirm, built as a LayerStack over the host's content —
// overlay = a ~62 % dark scrim (BlocksInput: no click/hover/wheel/drag reaches the content) with the house card centred
// on it, shown while the controller has a request open. Nothing is built per request: the card's texts and colours are
// live Funcs over ConfirmController.Current. The overlay covers the WHOLE window body below the title bar, edge to edge
// (BuildLayerStack coverBody → BodyFit): a host-sized scrim left the chrome's 12 px content padding as an undimmed frame
// around it. The card centres in that full body; uGUI blocking (the layer Image) and the ticker's LayerCovered both use
// the layer's rect, so the padding is blocked too. The title bar (✕, window drag) stays outside and live.
// Esc and "the host/window was hidden" cancel from the ticker's per-frame pass (WindowInteractionTicker.Layers.cs):
// the 10 Hz apply would miss a one-frame key edge, and a parked window runs no apply at all.
internal sealed partial class WindowBuilder
{
    private static readonly ColorRgba ConfirmScrim = new(0f, 0f, 0f, 0.62f);

    /// <summary>The house card for a confirm host (Host wires <c>UI.ConfirmCard.Build</c>: style kit + framework
    /// strings). Null in the sandbox → a plain fallback card.</summary>
    internal Func<ConfirmHostElement, HudElement>? ConfirmCard { get; set; }

    /// <summary>(host root, controller) → the ticker, for Esc / hidden-host cancel. Null in the sandbox.</summary>
    internal Action<GameObject, ConfirmController>? RegisterConfirmHost { get; set; }

    private void BuildConfirmHost(ConfirmHostElement h, Transform parent, WindowToken token)
    {
        var c = h.Controller;
        var card = ConfirmCard?.Invoke(h) ?? FallbackConfirmCard(c);
        var layer = new StackLayer(card)
        {
            Visible = () => c.IsOpen, BlocksInput = true, Background = () => ConfirmScrim, Center = true,
        };
        BuildLayerStack(new LayerStackElement(h.Content, layer), parent, token, coverBody: true);
        var stackRoot = parent.GetChild(parent.childCount - 1);   // the stack root just built
        RoundScrim(stackRoot, token);
        RegisterConfirmHost?.Invoke(stackRoot.gameObject, c);
    }

    // The scrim spans the window body (BodyFit), so its free corners meet the frame's rounded corners: give its Image the
    // frame radius — bottom corners only under a title bar (square top edge against the bar), all four without one.
    // Without this the square scrim corners poke out past the GlassMenu frame's rounding. Neutral white sprites (kept
    // across theme re-bakes), tinted by the layer's Background like before.
    private void RoundScrim(Transform stackRoot, WindowToken token)
    {
        if (token.Body == null || token.BodyRadius <= 0) return;
        var img = stackRoot.Find("Overlays/Layer1")?.GetComponent<UnityEngine.UI.Image>();
        if (img == null) return;
        var sprite = token.BodyTitleBar != null ? _assets.BubbleBgBottom(token.BodyRadius) : _assets.BubbleBg(token.BodyRadius);
        if (sprite == null) return;
        img.sprite = sprite; img.type = UnityEngine.UI.Image.Type.Sliced;
    }

    private static HudElement FallbackConfirmCard(ConfirmController c) => new ColumnElement(new HudElement[]
    {
        new TextElement(() => c.Current?.Title ?? "", Width: 300f) { Bold = true },
        new TextElement(() => c.Current?.Message ?? "", Width: 300f),
        new RowElement(new HudElement[]
        {
            new ButtonElement(() => c.Current?.CancelLabel ?? "Cancel", c.Cancel),
            new ButtonElement(() => c.Current?.ConfirmLabel ?? "OK", c.Confirm),
        }),
    });
}
