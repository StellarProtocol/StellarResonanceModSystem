using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

// ConfirmHostElement (framework 2.33.0): a window-scoped modal confirm, built as a LayerStack over the host's content —
// overlay = a ~62 % dark scrim (BlocksInput: no click/hover/wheel/drag reaches the content) with the house card centred
// on it, shown while the controller has a request open. Nothing is built per request: the card's texts and colours are
// live Funcs over ConfirmController.Current. Outside the host (title bar ✕, window drag) is untouched.
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
        BuildLayerStack(new LayerStackElement(h.Content, layer), parent, token);
        RegisterConfirmHost?.Invoke(parent.GetChild(parent.childCount - 1).gameObject, c);   // the stack root just built
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
