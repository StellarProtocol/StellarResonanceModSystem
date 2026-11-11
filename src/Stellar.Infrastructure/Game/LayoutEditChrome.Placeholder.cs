using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Native-UI PLACEHOLDER chrome for <see cref="LayoutEditChrome"/> — the approved prototype style A
/// (<c>_scratch/prototypes/native-ui-edit-placeholders.html</c>): a 2 px DASHED outline in the item colour (orange /
/// selected yellow), a translucent 135° hatched fill (8 px stripes, orange at ~10% / ~4% alpha) and a centred caption
/// (bold display name + an orange-tinted hint line + an optional muted sub line). The top-left "[Game UI] name" label
/// and the eye are the shared item parts. Dimmed 40% with the rest of the item when hidden by the user. All sprites
/// are baked in code (like the ring/handle/eye) and tiled, so the dash/stripe size stays constant at any box size.
/// </summary>
internal sealed partial class LayoutEditChrome
{
    private const int DashOn = 6, DashOff = 4;          // px — reads like the browser's 2 px "dashed" border
    private const int HatchTile = 22;                   // 22 px tile: a 135° stripe period of 22/√2 ≈ 15.6 px ≈ 8+8
    private const float HatchStrongAlpha = 0.10f;       // stripe alphas from the prototype (orange 10% / 4%)
    private const float HatchWeakRatio = 0.40f;         // 0.04 / 0.10, baked into the texture
    private static readonly Color CaptionNameColor = new(0.97f, 0.90f, 0.81f);   // #f7e6cf
    private const string HintHex = "f5c27a";            // orange-tinted hint line
    private const string SubHex  = "cdbfd6";            // muted sub line

    private Sprite? _dashHSprite, _dashVSprite, _hatchSprite;

    private sealed class Placeholder
    {
        public Image Hatch = null!;
        public Image[] Dashes = null!;   // top, bottom, left, right
        public Text Caption = null!;
        public string? CapName, CapHint, CapSub;   // last caption inputs — rebuild the rich string only on change
        public bool CapDim;
        public bool Shown = true;
    }

    private Placeholder BuildPlaceholder(Transform root)
    {
        var hatch = NewChild("PhHatch", root);
        Stretch(hatch.GetComponent<RectTransform>());
        hatch.transform.SetAsFirstSibling();   // under the ring / handle / label
        var hImg = hatch.AddComponent<Image>();
        hImg.sprite = _hatchSprite; hImg.type = Image.Type.Tiled; hImg.raycastTarget = false;

        var dashes = new[] { Side(root, 0), Side(root, 1), Side(root, 2), Side(root, 3) };   // top, bottom, left, right

        var cap = NewChild("PhCaption", root);
        var crt = cap.GetComponent<RectTransform>();
        Stretch(crt);
        var txt = cap.AddComponent<Text>();
        txt.fontSize = 11; txt.alignment = TextAnchor.MiddleCenter; txt.supportRichText = true;
        txt.color = CaptionNameColor; txt.raycastTarget = false; txt.lineSpacing = 1.05f;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow; txt.verticalOverflow = VerticalWrapMode.Overflow;
        txt.font = _font != null ? _font : BuiltinFont();
        var shadow = cap.AddComponent<Shadow>();   // the prototype's 0 1px 2px #000 text-shadow
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f); shadow.effectDistance = new Vector2(1f, -1f);

        var ph = new Placeholder { Hatch = hImg, Dashes = dashes, Caption = txt };
        SetPlaceholderShown(ph, false);
        return ph;
    }

    // One 2 px dashed side pinned to an edge of the box: 0 top, 1 bottom, 2 left, 3 right.
    private Image Side(Transform root, int side)
    {
        var horizontal = side < 2;
        var go = NewChild(side switch { 0 => "PhTop", 1 => "PhBottom", 2 => "PhLeft", _ => "PhRight" }, root);
        var rt = go.GetComponent<RectTransform>();
        var edge = side == 0 || side == 3 ? 1f : 0f;   // top / right sit on the 1-edge of their axis
        rt.anchorMin = horizontal ? new Vector2(0f, edge) : new Vector2(edge, 0f);
        rt.anchorMax = horizontal ? new Vector2(1f, edge) : new Vector2(edge, 1f);
        rt.pivot = horizontal ? new Vector2(0.5f, edge) : new Vector2(edge, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = horizontal ? new Vector2(0f, OutlineThickness) : new Vector2(OutlineThickness, 0f);
        var img = go.AddComponent<Image>();
        img.sprite = horizontal ? _dashHSprite : _dashVSprite; img.type = Image.Type.Tiled; img.raycastTarget = false;
        return img;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    // Per tick: a placeholder swaps the solid ring for dashes + hatch + caption; a live item does the reverse.
    private void PositionPlaceholder(Item it, in EditChromeItem data, float a)
    {
        var ph = it.Ph;
        it.Border.enabled = !data.Placeholder;
        SetPlaceholderShown(ph, data.Placeholder);
        if (!data.Placeholder) return;
        var c = Fade(data.Color, a);
        for (var i = 0; i < ph.Dashes.Length; i++) ph.Dashes[i].color = c;
        ph.Hatch.color = new Color(data.Color.r, data.Color.g, data.Color.b, HatchStrongAlpha * a);
        ph.Caption.color = Fade(CaptionNameColor, a);
        var dim = a < 1f;
        if (ph.CapName == data.Caption && ph.CapHint == data.Hint && ph.CapSub == data.SubHint && ph.CapDim == dim) return;
        ph.CapName = data.Caption; ph.CapHint = data.Hint; ph.CapSub = data.SubHint; ph.CapDim = dim;
        ph.Caption.text = BuildCaption(data.Caption, data.Hint, data.SubHint, dim);
    }

    // "<b>Name</b>\n<size=10><color=#hint>Hint</color></size>[\n<size=10><color=#sub>Sub</color></size>]". Rich colour
    // tags carry their own alpha, so the 40% dim is baked into them (66 = 0.4) rather than relying on the Text colour.
    private static string BuildCaption(string? name, string? hint, string? sub, bool dim)
    {
        var alpha = dim ? "66" : "ff";
        var s = "<b>" + (name ?? "") + "</b>";
        if (!string.IsNullOrEmpty(hint)) s += "\n<size=10><color=#" + HintHex + alpha + ">" + hint + "</color></size>";
        if (!string.IsNullOrEmpty(sub))  s += "\n<size=10><color=#" + SubHex + alpha + ">" + sub + "</color></size>";
        return s;
    }

    private static void SetPlaceholderShown(Placeholder ph, bool shown)
    {
        if (ph.Shown == shown) return;
        ph.Shown = shown;
        ph.Hatch.enabled = shown;
        ph.Caption.enabled = shown;
        for (var i = 0; i < ph.Dashes.Length; i++) ph.Dashes[i].enabled = shown;
    }

    private void EnsurePlaceholderSprites()
    {
        if (_dashHSprite == null) _dashHSprite = BakeDash(horizontal: true);
        if (_dashVSprite == null) _dashVSprite = BakeDash(horizontal: false);
        if (_hatchSprite == null) _hatchSprite = BakeHatch();
    }

    // One dash period (on + off) along the side, 2 px thick; tiled along the side's length. White = tinted at draw.
    private static Sprite BakeDash(bool horizontal)
    {
        const int period = DashOn + DashOff;
        int w = horizontal ? period : (int)OutlineThickness, h = horizontal ? (int)OutlineThickness : period;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                t.SetPixel(x, y, (horizontal ? x : y) < DashOn ? Color.white : Color.clear);
        t.Apply();
        return Sprite.Create(t, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    // 135° stripes (CSS repeating-linear-gradient(135deg) runs toward the bottom-right, so stripes lie along the "/"
    // diagonal: constant (x − y) in texture space, y-up). Strong stripe alpha 1, weak HatchWeakRatio; tinted to the
    // prototype's 10% / 4% orange via the Image colour. Seamless: the period divides the tile in both axes.
    private static Sprite BakeHatch()
    {
        var t = new Texture2D(HatchTile, HatchTile, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
        for (int y = 0; y < HatchTile; y++)
            for (int x = 0; x < HatchTile; x++)
            {
                var band = ((x - y) % HatchTile + HatchTile) % HatchTile;
                t.SetPixel(x, y, new Color(1f, 1f, 1f, band < HatchTile / 2 ? 1f : HatchWeakRatio));
            }
        t.Apply();
        return Sprite.Create(t, new Rect(0, 0, HatchTile, HatchTile), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }
}
