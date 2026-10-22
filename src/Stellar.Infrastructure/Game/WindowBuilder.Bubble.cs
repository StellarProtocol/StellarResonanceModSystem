using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// BubbleElement rendering: a content-hugging, fill-tinted rounded "chat bubble" card. Kept in its own partial so
// WindowBuilder.cs / .Table.cs stay under the file-size gate. The width solve (hug-up-to-cap, then wrap) is done
// by a per-apply BubbleBinding (WindowBuilder.Bindings.Misc.cs); everything static is built here.
internal sealed partial class WindowBuilder
{
    // A rounded fill-tinted card that hugs its child's width. The child is hosted in a padded VLG; a vertical
    // ContentSizeFitter grows the bubble's HEIGHT to the (wrapped) content. The WIDTH is NOT driven by a CSF —
    // CSF.PreferredSize would use the child's UNWRAPPED width (never wrapping); instead BubbleBinding reads the
    // child's natural preferred width each apply and pins the bubble's LayoutElement to min(natural, cap), so the
    // bubble hugs a short child and fixes at the cap for a long one (its wrapping Text then flows to >1 line).
    //
    // Content-hug requires the parent layout to respect the child's width: a RowElement's HLG does
    // (childForceExpandWidth=false); a ColumnElement force-expands width, so a bubble dropped straight into a
    // Column fills the column instead of hugging (documented on BubbleElement — mirror the icon-in-a-Row gotcha).
    private void BuildBubble(BubbleElement bubble, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Bubble", parent);

        // Width is binding-driven: flexibleWidth 0 so a Row HLG never stretches it, and min/preferredWidth set by
        // BubbleBinding. (Seeded once below so the first layout pass isn't a 0-width flash for a simple text child.)
        var le = go.AddComponent<LayoutElement>();
        le.flexibleWidth = 0f;

        var bg = AddBubbleBackground(go, bubble, token);

        // Padded child host. childForceExpandWidth so the (pinned-width) bubble stretches a wrapping Text child to
        // its inner width → the Text wraps; CSF grows the height. Mirrors BuildSelectable's host, sans the Button.
        var padX = Mathf.RoundToInt(bubble.PaddingX);
        var padY = Mathf.RoundToInt(bubble.PaddingY);
        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(padX, padX, padY, padY); vlg.spacing = 0f;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperLeft;
        var csf = go.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;   // width is binding-driven, not CSF-driven
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var childIdx = go.transform.childCount;            // the single content child BuildElement is about to add
        BuildElement(bubble.Child, go.transform, token);
        var child = childIdx < go.transform.childCount ? go.transform.GetChild(childIdx) as RectTransform : null;

        var binding = new BubbleBinding
        {
            Le = le, Child = child!, Parent = parent as RectTransform,
            MaxWidth = bubble.MaxWidth, PadX = padX * 2f,   // total horizontal padding (both sides)
        };
        // Per-refresh fill colour (BubbleElement.FillColorFn, mirroring BarElement.FillColorFn): attach the override
        // ONLY when set, so a bubble that leaves it null keeps the static fill painted above with no machinery.
        if (bubble.FillColorFn != null) { binding.FillGraphic = bg; binding.FillColorFn = bubble.FillColorFn; }
        token.Bubbles.Add(binding);
        binding.Apply();        // seed the width (and the first fill-colour poll) before the window's first layout rebuild
    }

    // The rounded SLICED background (the same SwatchBg chip BuildSelectable uses, or a baked rounder sprite when
    // CornerRadius > 0), tinted to FillColor with its ALPHA kept so translucent bubbles work. raycastTarget=false
    // → the bubble is click-through-neutral (never eats clicks meant for a Selectable wrapping it, or behind it).
    // Null FillColor = a themed menu-accent default, re-tinted on a theme change so the four presets track.
    // Returns the background Image (the bubble's fill Graphic) so BuildBubble can hand it to the BubbleBinding
    // for a per-refresh FillColorFn re-tint (mirrors how BuildBarTrack returns the bar's fill Graphic).
    private Image AddBubbleBackground(GameObject go, BubbleElement bubble, WindowToken token)
    {
        var bg = go.AddComponent<Image>();
        var sprite = _assets.BubbleBg(Mathf.RoundToInt(bubble.CornerRadius));  // null in a pre-bake sandbox
        if (sprite != null) { bg.sprite = sprite; bg.type = Image.Type.Sliced; }
        bg.raycastTarget = false;
        var fill = bubble.FillColor;
        Color Tint() => fill is { } f
            ? new Color(f.R, f.G, f.B, f.A)
            : new Color(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.28f);
        bg.color = Tint();
        if (fill == null) token.ReskinActions.Add(() => { if (bg != null) bg.color = Tint(); });
        return bg;
    }
}
