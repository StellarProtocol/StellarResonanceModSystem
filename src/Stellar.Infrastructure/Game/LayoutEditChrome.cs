using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>One editable element's edit-mode chrome request: its screen rect (top-left / y-down domain
/// coords, same as IInputGateway.PointerPosition), the outline/handle colour (orange unselected / yellow
/// selected / red errored), and the label.</summary>
internal readonly struct EditChromeItem
{
    public readonly WindowRect Rect;
    public readonly Color Color;
    public readonly string Label;
    public readonly string Id;
    public readonly bool Visible;
    public readonly bool CanHide;
    public EditChromeItem(WindowRect rect, Color color, string label, string id, bool visible, bool canHide)
    { Rect = rect; Color = color; Label = label; Id = id; Visible = visible; CanHide = canHide; }
}

/// <summary>
/// uGUI renderer for layout edit-mode chrome — a screen-space overlay canvas with a grow-only pool of
/// per-element outline (hollow border) + corner handle + label, repositioned each tick from the live rects.
/// Replaces the IMGUI GUI.DrawTexture/GUI.Label path so edit-mode survives with IMGUI off (Stage B of the
/// layout-editor uGUI migration). Sandbox-pure (UnityEngine only, no IL2CPP/BepInEx) so it renders headlessly.
///
/// <para>The single y-flip site: domain rects are top-left/y-down; the canvas RectTransforms are bottom-up.
/// A top-left anchor+pivot with <c>anchoredPosition = (X, -Y)</c> places the box at screen (X,Y) — the same
/// idiom as HudRenderer.SetRect. All Images/Text are <c>raycastTarget=false</c> + the canvas has no
/// GraphicRaycaster, so the overlay is transparent to the pointer (edit-drag hit-tests manual rects).</para>
/// </summary>
internal sealed class LayoutEditChrome
{
    private const int SortingOrder = 32758;   // HUD 32750 < Window 32755 < this < input-blocker 32760
    private const float OutlineThickness = 2f;
    private const float HandleSize = 8f;
    private static readonly Color LabelColor = new(0.37f, 0.91f, 0.77f);   // mint, matches the IMGUI label

    private GameObject? _canvas;
    private Transform? _root;
    private Sprite? _borderSprite;
    private Sprite? _handleSprite;
    private Sprite? _eyeOpenSprite;
    private Sprite? _eyeClosedSprite;
    private Font? _font;
    private readonly List<Item> _pool = new();

    private sealed class Item
    {
        public GameObject Root = null!;
        public RectTransform Rt = null!;
        public Image Border = null!;
        public Image Handle = null!;
        public Text Label = null!;
        public Image Eye = null!;
        public string Id = "";
        public Rect EyeScreen;       // top-left / y-down screen rect of the eye, for the editor's manual hit-test
        public bool EyeInteractive;  // false for unsafe-to-hide (greyed) elements
    }

    /// <summary>Optional label font (the menu OS font); null falls back to the builtin.</summary>
    public void SetFont(Font? font) => _font = font;

    public bool EnsureCanvas()
    {
        if (_canvas != null) return true;
        var go = new GameObject("StellarLayoutEditCanvas") { hideFlags = HideFlags.HideAndDontSave };
        Object.DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        // No GraphicRaycaster: the overlay is pure decoration; edit-drag uses IInputGateway + manual rects.
        EnsureSprites();
        _canvas = go;
        _root = go.transform;
        return true;
    }

    /// <summary>Show chrome for exactly <paramref name="items"/> — grow the pool as needed, reposition/tint
    /// the visible prefix, park the surplus. Re-creates the canvas if a scene change destroyed it.</summary>
    public void Sync(IReadOnlyList<EditChromeItem> items)
    {
        if (!EnsureCanvas()) return;
        for (int i = 0; i < items.Count; i++)
        {
            if (_pool.Count <= i) _pool.Add(BuildItem());
            Position(_pool[i], items[i]);
            if (!_pool[i].Root.activeSelf) _pool[i].Root.SetActive(true);
        }
        for (int i = items.Count; i < _pool.Count; i++)
            if (_pool[i].Root.activeSelf) _pool[i].Root.SetActive(false);
    }

    public void Teardown()
    {
        if (_canvas != null) Object.Destroy(_canvas);
        _canvas = null;
        _root = null;
        _pool.Clear();
    }

    private void Position(Item it, in EditChromeItem data)
    {
        var r = data.Rect;
        // y-flip: top-left anchor+pivot, anchoredPosition (X, -Y) → box at screen (X, Y) top-down.
        it.Rt.anchoredPosition = new Vector2(r.X, -r.Y);
        it.Rt.sizeDelta = new Vector2(r.Width, r.Height);
        // Dim the whole chrome (outline + handle + label) when hidden, so it reads as a re-enable ghost.
        var a = data.Visible ? 1f : 0.40f;
        it.Border.color = Fade(data.Color, a);
        it.Handle.color = Fade(data.Color, a);
        it.Label.color  = Fade(LabelColor, a);
        it.Label.text   = data.Label;

        // Eye: open when visible, closed when hidden; greyed + non-interactive when unsafe-to-hide.
        it.Eye.sprite = data.Visible ? _eyeOpenSprite : _eyeClosedSprite;
        it.Eye.color  = data.CanHide ? new Color(0.85f, 0.95f, 0.90f, data.Visible ? 1f : 0.6f)
                                     : new Color(0.5f, 0.5f, 0.5f, 0.5f);
        it.Id = data.Id;
        it.EyeInteractive = data.CanHide;
        // Place the eye just right of the label text (label pivot is bottom-left, anchored 2px in from the box
        // top-left; preferredWidth tracks the text). Then record its screen-rect (top-left / y-down) for hit-test.
        var eyeLocalX = it.Label.preferredWidth + 4f;
        it.Eye.rectTransform.anchoredPosition = new Vector2(eyeLocalX, 0f);
        it.EyeScreen = new Rect(r.X + 2f + eyeLocalX, r.Y - 16f, 13f, 13f);
    }

    private static Color Fade(Color c, float a) => new(c.r, c.g, c.b, c.a * a);

    private Item BuildItem()
    {
        var root = NewChild("EditChrome", _root!);
        var rt = root.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);

        // Border: hollow 9-slice frame stretched to fill the box (fillCenter=false → only the 2px ring draws).
        var border = NewChild("Border", root.transform);
        var brt = border.GetComponent<RectTransform>();
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
        var bImg = border.AddComponent<Image>();
        bImg.sprite = _borderSprite; bImg.type = Image.Type.Sliced; bImg.fillCenter = false; bImg.raycastTarget = false;

        // Handle: solid square pinned to the box's bottom-right corner (uGUI (1,0) = screen bottom-right here).
        var handle = NewChild("Handle", root.transform);
        var hrt = handle.GetComponent<RectTransform>();
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(1f, 0f);
        hrt.sizeDelta = new Vector2(HandleSize, HandleSize); hrt.anchoredPosition = Vector2.zero;
        var hImg = handle.AddComponent<Image>();
        hImg.sprite = _handleSprite; hImg.raycastTarget = false;

        // Label: sits just above the box's top-left (anchor top-left, pivot bottom-left so it extends upward).
        var label = NewChild("Label", root.transform);
        var lrt = label.GetComponent<RectTransform>();
        lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 1f); lrt.pivot = new Vector2(0f, 0f);
        lrt.sizeDelta = new Vector2(220f, 16f); lrt.anchoredPosition = new Vector2(2f, 2f);
        var txt = label.AddComponent<Text>();
        txt.fontSize = 11; txt.fontStyle = FontStyle.Bold; txt.color = LabelColor; txt.raycastTarget = false;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow; txt.verticalOverflow = VerticalWrapMode.Overflow;
        txt.font = _font != null ? _font : BuiltinFont();

        // Eye toggle: child of the label so it follows the text origin; positioned right of the text in
        // Position(). raycastTarget=false (canvas has no GraphicRaycaster) — the editor hit-tests EyeScreen.
        var eye = NewChild("Eye", label.transform);
        var ert = eye.GetComponent<RectTransform>();
        ert.anchorMin = ert.anchorMax = new Vector2(0f, 0.5f); ert.pivot = new Vector2(0f, 0.5f);
        ert.sizeDelta = new Vector2(13f, 13f); ert.anchoredPosition = Vector2.zero;
        var eImg = eye.AddComponent<Image>();
        eImg.sprite = _eyeOpenSprite; eImg.raycastTarget = false;

        return new Item { Root = root, Rt = rt, Border = bImg, Handle = hImg, Label = txt, Eye = eImg };
    }

    /// <summary>True if (x,y) — top-left / y-down screen coords, same domain as IInputGateway.PointerPosition —
    /// is over a visible, INTERACTIVE eye icon; out the owning element id. Lets the editor route a visibility
    /// toggle without a GraphicRaycaster on this overlay canvas.</summary>
    public bool TryGetEyeHit(float x, float y, out string id)
    {
        for (int i = 0; i < _pool.Count; i++)
        {
            var it = _pool[i];
            if (!it.Root.activeSelf || !it.EyeInteractive) continue;
            if (it.EyeScreen.Contains(new Vector2(x, y))) { id = it.Id; return true; }
        }
        id = ""; return false;
    }

    private void EnsureSprites()
    {
        if (_borderSprite == null)
        {
            var t = new Texture2D(8, 8, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    bool edge = x < OutlineThickness || x >= 8 - OutlineThickness || y < OutlineThickness || y >= 8 - OutlineThickness;
                    t.SetPixel(x, y, edge ? Color.white : Color.clear);
                }
            t.Apply();
            _borderSprite = Sprite.Create(t, new Rect(0, 0, 8, 8), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(OutlineThickness, OutlineThickness, OutlineThickness, OutlineThickness));
        }
        if (_handleSprite == null)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, Color.white); t.Apply();
            _handleSprite = Sprite.Create(t, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }
        if (_eyeOpenSprite == null)   _eyeOpenSprite   = BakeEye(open: true);
        if (_eyeClosedSprite == null) _eyeClosedSprite = BakeEye(open: false);
    }

    // A crude but readable 16×16 eye: a wide almond outline + (open) a centred pupil / (closed) a horizontal
    // slit. White pixels are tinted at draw time; baked once like the border/handle sprites (no font glyph).
    private static Sprite BakeEye(bool open)
    {
        const int N = 16;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = (x - 7.5f) / 7.5f, dy = (y - 7.5f) / 4.0f;     // wide almond
                bool inEye = dx * dx + dy * dy <= 1f;
                bool ring  = inEye && dx * dx + dy * dy >= 0.62f;          // almond outline
                bool pupil = open && (x - 7.5f) * (x - 7.5f) + (y - 7.5f) * (y - 7.5f) <= 6.5f;
                bool slit  = !open && y >= 7 && y <= 8 && inEye;           // closed = slit bar
                t.SetPixel(x, y, (ring || pupil || slit) ? Color.white : Color.clear);
            }
        t.Apply();
        return Sprite.Create(t, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
    }

    // Reuse the IL2CPP-safe primitive (a raw `new GameObject(name, typeof(RectTransform))` doesn't bind under
    // Il2CppInterop) — also keeps RectTransform creation consistent with the rest of the uGUI builders.
    private static GameObject NewChild(string name, Transform parent) => UGuiPrimitives.NewChild(name, parent);

    private static Font? BuiltinFont()
    {
        try { return Resources.GetBuiltinResource<Font>("Arial.ttf"); }
        catch { return null; }
    }
}
