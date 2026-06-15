using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder HSV ColorPicker — the one custom widget. STATIC VISUAL: preview swatch + an SV-square
/// RawImage (baked for the current hue) + a hue-bar RawImage + a hex field, with two-tone dot markers at
/// the current H/S/V (reusing the gradient formulas from ThemeEditorBody.Hsv). DRAG is deferred
/// (renderer-side Input poll / injected IDragHandler — batchmode can't exercise it). Baked textures are
/// HideAndDontSave; per-build lifetime — full reuse/destroy tracking is a flagged follow-up (Plan 4/refine).
/// Sizes from the Measurement contract: preview 52×88, SV 150×88, hue 212×16.
/// </summary>
internal sealed partial class WindowBuilder
{
    private const int SvTexN = 64;
    private const int HueTexN = 128;

    // One shared HSV picker per window (not one per slot — that would bake N textures + leak). A
    // ColorPickerBinding re-syncs the SV square + markers + preview whenever the bound colour changes
    // EXTERNALLY (the theme editor re-points Get() at a different slot, or a hex is typed), while leaving
    // drag-driven changes alone. This makes a single picker work across every overridable slot.
    private void BuildColorPicker(ColorPickerElement cp, Transform parent, WindowToken token)
    {
        var c = cp.Get();
        var binding = new ColorPickerBinding { Builder = this, Get = cp.Get, Set = cp.Set };
        Color.RGBToHSV(new Color(c.R, c.G, c.B), out binding.H, out binding.S, out binding.V);

        var col = UGuiPrimitives.NewChild("ColorPicker", parent);
        UGuiPrimitives.AddLayout(col, gap: RowGap, columns: UGuiPrimitives.ColumnMode);

        var row = UGuiPrimitives.NewChild("SvRow", col.transform);
        UGuiPrimitives.AddLayout(row, gap: 8f, columns: UGuiPrimitives.RowMode);

        var prev = UGuiPrimitives.NewChild("Preview", row.transform);
        var ple = prev.AddComponent<LayoutElement>(); ple.preferredWidth = 52f; ple.preferredHeight = 88f; ple.flexibleWidth = 0f;
        binding.Preview = prev.AddComponent<Image>();
        binding.Preview.color = new Color(c.R, c.G, c.B, 1f); binding.Preview.raycastTarget = false;

        var sv = UGuiPrimitives.NewChild("SV", row.transform);
        var sle = sv.AddComponent<LayoutElement>(); sle.preferredWidth = 150f; sle.preferredHeight = 88f; sle.flexibleWidth = 0f;
        binding.SvRaw = sv.AddComponent<RawImage>(); binding.SvTex = BakeSv(binding.H);
        binding.SvRaw.texture = binding.SvTex; binding.SvRaw.raycastTarget = true;
        binding.SvMarker = AddMarker(sv.transform, binding.S, 1f - binding.V);

        var hue = UGuiPrimitives.NewChild("Hue", col.transform);
        var hle = hue.AddComponent<LayoutElement>(); hle.preferredWidth = 212f; hle.preferredHeight = 16f; hle.flexibleWidth = 0f;
        var hueRaw = hue.AddComponent<RawImage>(); binding.HueTex = BakeHue(); hueRaw.texture = binding.HueTex; hueRaw.raycastTarget = true;
        binding.HueMarker = AddMarker(hue.transform, binding.H, 0.5f);

        // Drag wiring (renderer's interaction ticker polls these; null in the sandbox → static render).
        // SV square → set S/V at the current hue; hue bar → set H, rebake the SV square. Alpha preserved.
        _registerDrag?.Invoke(sv.GetComponent<RectTransform>(), (nx, ny) => binding.OnDragSv(nx, ny));
        _registerDrag?.Invoke(hue.GetComponent<RectTransform>(), (nx, _) => binding.OnDragHue(nx));

        token.Pickers.Add(binding);
        BuildPickerControls(col, cp, c, token, binding);
    }

    // Re-syncs the shared picker to the bound colour on external change; owns the per-hue SV texture.
    internal sealed class ColorPickerBinding
    {
        public WindowBuilder Builder = null!;
        public Func<ColorRgba> Get = null!;
        public Action<ColorRgba> Set = null!;
        public Image Preview = null!;
        public RawImage SvRaw = null!;
        public Texture2D? SvTex;
        public Texture2D? HueTex;
        public RectTransform SvMarker = null!, HueMarker = null!;
        public UGuiTextInput? HexField;   // re-seeded on external change so it doesn't show the prior slot's hex
        public float H, S, V;
        private bool _init; private ColorRgba _lastApplied;

        // The baked SV/hue textures use HideFlags.HideAndDontSave, so they are exempt from UnloadUnusedAssets
        // AND from GameObject destruction — WindowRenderer.Destroy(token.Root) won't reclaim them. Destroy them
        // explicitly when the window is torn down (called from WindowToken.DisposeNativeTextures), else every
        // Settings close/reopen + scene-change self-heal remount leaks a 64²+128 texture pair.
        public void Destroy()
        {
            if (SvTex != null) UnityEngine.Object.Destroy(SvTex);
            if (HueTex != null) UnityEngine.Object.Destroy(HueTex);
            SvTex = null; HueTex = null;
        }

        public void OnDragSv(float nx, float ny)
        {
            S = Mathf.Clamp01(nx); V = Mathf.Clamp01(ny);
            Commit();
            SvMarker.anchorMin = SvMarker.anchorMax = new Vector2(S, V);
        }

        public void OnDragHue(float nx)
        {
            H = Mathf.Clamp01(nx);
            RebakeSv();
            Commit();
            HueMarker.anchorMin = HueMarker.anchorMax = new Vector2(H, 0.5f);
        }

        private void Commit()
        {
            var rgb = Color.HSVToRGB(H, S, V);
            var c = new ColorRgba(rgb.r, rgb.g, rgb.b, Get().A);
            _lastApplied = c; _init = true;
            Set(c);
            Preview.color = new Color(rgb.r, rgb.g, rgb.b, 1f);
        }

        private void RebakeSv()
        {
            if (SvTex != null) UnityEngine.Object.Destroy(SvTex);
            SvTex = Builder.BakeSv(H); SvRaw.texture = SvTex;
        }

        // Poll: if the bound colour changed from OUTSIDE the picker (slot switch / hex entry / alpha drag),
        // re-derive H/S/V, reposition the markers + preview, and rebake the SV square ONLY when the hue
        // actually moved (an opacity-only drag changes alpha, not hue — rebaking 64² every frame would thrash).
        public void Apply()
        {
            if (SvRaw == null || !SvRaw.gameObject.activeInHierarchy) return;   // skip while the picker is collapsed
            var c = Get();
            if (_init && c.Equals(_lastApplied)) return;
            _lastApplied = c; _init = true;
            var prevH = H;   // H holds the build-time / last-synced hue until overwritten below
            Color.RGBToHSV(new Color(c.R, c.G, c.B), out H, out S, out V);
            if (!Mathf.Approximately(prevH, H)) RebakeSv();
            SvMarker.anchorMin = SvMarker.anchorMax = new Vector2(S, V);
            HueMarker.anchorMin = HueMarker.anchorMax = new Vector2(H, 0.5f);
            Preview.color = new Color(c.R, c.G, c.B, 1f);
            // Re-seed the hex box to the current colour, but not while the user is typing in it (don't fight input).
            if (HexField != null && !HexField.IsFocused) HexField.SetText(ToHex(c));
        }
    }

    // Opacity (alpha) slider + hex field below the SV/hue. Opacity is the alpha control (SV/hue are
    // opaque-only — matches the IMGUI DrawOpacityRow); hex is the precise-entry path (6/8-digit).
    private void BuildPickerControls(GameObject col, ColorPickerElement cp, ColorRgba seed, WindowToken token, ColorPickerBinding binding)
    {
        var opacity = UGuiPrimitives.NewChild("Opacity", col.transform);
        UGuiPrimitives.AddLayout(opacity, gap: 6f, columns: UGuiPrimitives.RowMode);
        BuildText(new TextElement(() => "Opacity"), opacity.transform, token);
        BuildSlider(new SliderElement(() => cp.Get().A,
            a => { var g = cp.Get(); cp.Set(new ColorRgba(g.R, g.G, g.B, a)); }, 0f, 1f), opacity.transform, token);
        BuildText(new TextElement(() => $"{Mathf.RoundToInt(cp.Get().A * 100f)}%"), opacity.transform, token);

        var hexRow = UGuiPrimitives.NewChild("HexRow", col.transform);
        UGuiPrimitives.AddLayout(hexRow, gap: 6f, columns: UGuiPrimitives.RowMode);
        var lblGo = UGuiPrimitives.NewChild("HexLabel", hexRow.transform);
        var lbl = lblGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(lbl, Scaled(12), TextAnchor.MiddleLeft, bold: false);
        ApplyMenuFont(lbl);
        lbl.color = _assets.MenuMuted; lbl.text = "Hex";
        RegisterTextReskin(token, lbl, 12, muted: true);

        var set = cp.Set;
        var field = new UGuiTextInput(onSubmit: hex => { if (TryParseHex(hex, cp.Get().A, out var nc)) set(nc); });
        var fgo = field.Build(hexRow.transform);
        (fgo.GetComponent<LayoutElement>() ?? fgo.AddComponent<LayoutElement>()).preferredWidth = 90f;
        field.SetFont(_assets.MenuFont);
        field.SetText(ToHex(seed));
        field.ApplyStyle(_assets.Capsule, new Color(0.05f, 0.07f, 0.10f, 0.95f), new Color(0.92f, 0.94f, 0.97f, 1f), 8f);
        binding.HexField = field;   // so ColorPickerBinding.Apply re-seeds it when the bound slot changes
        _registerField?.Invoke(field);
        token.Fields.Add(field);
    }

    private Texture2D BakeSv(float hue)
    {
        var tex = new Texture2D(SvTexN, SvTexN, TextureFormat.RGBA32, mipChain: false)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color[SvTexN * SvTexN];
        for (var y = 0; y < SvTexN; y++)
            for (var x = 0; x < SvTexN; x++)
                px[y * SvTexN + x] = Color.HSVToRGB(hue, (float)x / (SvTexN - 1), (float)y / (SvTexN - 1));
        tex.SetPixels(px); tex.Apply();
        return tex;
    }

    private Texture2D BakeHue()
    {
        var tex = new Texture2D(HueTexN, 1, TextureFormat.RGBA32, mipChain: false)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color[HueTexN];
        for (var x = 0; x < HueTexN; x++) px[x] = Color.HSVToRGB((float)x / (HueTexN - 1), 1f, 1f);
        tex.SetPixels(px); tex.Apply();
        return tex;
    }

    // Two-tone dot marker (dark halo + light core) at normalised (nx, ny-from-top) — reads on any colour.
    // Returns the halo RectTransform so drag can reposition it (anchorMin/Max).
    private RectTransform AddMarker(Transform parent, float nx, float nyFromTop)
    {
        var d = UGuiPrimitives.NewChild("MarkHalo", parent);
        d.AddComponent<LayoutElement>().ignoreLayout = true;
        var drt = d.GetComponent<RectTransform>();
        drt.anchorMin = drt.anchorMax = new Vector2(nx, 1f - nyFromTop); drt.pivot = new Vector2(0.5f, 0.5f);
        drt.sizeDelta = new Vector2(11f, 11f); drt.anchoredPosition = Vector2.zero;
        var dimg = d.AddComponent<Image>(); dimg.sprite = _assets.Capsule; dimg.type = Image.Type.Sliced;
        dimg.color = new Color(0f, 0f, 0f, 0.7f); dimg.raycastTarget = false;
        var l = UGuiPrimitives.NewChild("MarkCore", d.transform);
        UGuiPrimitives.Stretch(l);
        var lrt = l.GetComponent<RectTransform>(); lrt.offsetMin = new Vector2(2f, 2f); lrt.offsetMax = new Vector2(-2f, -2f);
        var limg = l.AddComponent<Image>(); limg.sprite = _assets.Capsule; limg.type = Image.Type.Sliced;
        limg.color = Color.white; limg.raycastTarget = false;
        return drt;
    }

    // #RRGGBB when fully opaque; #RRGGBBAA when transparent (matches the IMGUI ThemeEditorBody.ToHex).
    private static int C255(float f) => Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);
    private static string ToHex(ColorRgba c)
    {
        var rgb = $"#{C255(c.R):X2}{C255(c.G):X2}{C255(c.B):X2}";
        var a = C255(c.A);
        return a == 255 ? rgb : rgb + a.ToString("X2", CultureInfo.InvariantCulture);
    }

    // Accepts 6-digit (RRGGBB; keeps the current alpha) or 8-digit (RRGGBBAA; parses alpha).
    private static bool TryParseHex(string? hex, float alpha, out ColorRgba c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var t = hex.Trim().TrimStart('#');
        if (t.Length != 6 && t.Length != 8) return false;
        if (!int.TryParse(t.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)) return false;
        if (!int.TryParse(t.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)) return false;
        if (!int.TryParse(t.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false;
        var a = alpha;
        if (t.Length == 8 && int.TryParse(t.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ai)) a = ai / 255f;
        c = new ColorRgba(r / 255f, g / 255f, b / 255f, a);
        return true;
    }
}
