using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Login-sidebar (title-screen) button styling for <see cref="PandaUGuiAdapter"/> — kept in a sibling partial.
/// The native login buttons (Settings / Switch-account / …) are a dark translucent CIRCLE behind a white
/// mono glyph, unlike the in-world Main-Menu rail (accent-tinted glowing star). This partial recreates that
/// look for the injected Stellar button, and places it via the sidebar's VerticalLayoutGroup. See
/// Knowledge Base/Login-Screen-UI-Injection.md.
/// </summary>
internal sealed partial class PandaUGuiAdapter
{
    // Shared black AA disc for login-sidebar buttons. Generated once; HideAndDontSave so a scene load's
    // UnloadUnusedAssets sweep doesn't blank it (same discipline as the icon cache). Recreated if destroyed.
    private Texture2D? _circleTex;

    private Texture2D CircleTex() => _circleTex != null ? _circleTex : (_circleTex = MakeAaDisc(64));

    // A WHITE antialiased disc (rgb=white, alpha=coverage) — tinted at draw time by the RawImage colour
    // (multiply), so drawing it black @ ~0.5 alpha yields the native dark-circle background. 1px edge falloff.
    private static Texture2D MakeAaDisc(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
        { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        float c = size / 2f, r = size / 2f - 1f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x + 0.5f) - c, dy = (y + 0.5f) - c;
            float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy));
            px[y * size + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    // Destroys the login-circle texture on framework teardown (called from Dispose alongside the icon cache).
    private void DestroyCircleTex()
    {
        if (_circleTex == null) return;
        UnityEngine.Object.Destroy(_circleTex);
        _circleTex = null;
    }

    // Builds the login-sidebar button: transparent click surface + black ~50% circle + a WHITE-drawn icon
    // (the stellar art is monochrome, so a white RawImage tint renders it as a white glyph — matching the
    // native sidebar buttons). Built fresh (never cloned — a clone drags the game's data-binder), under the
    // sidebar 'layout' container that owns a VerticalLayoutGroup.
    private GameObject BuildLoginSidebarButton(GameObject go, MenuButtonSpec spec, Vector2 size, Transform template)
    {
        AddSolid(go, new Color(0f, 0f, 0f, 0f));   // 0-alpha surface still raycasts → owns the click

        float disc = Mathf.Min(size.x, size.y) * 0.86f;
        AddRawImage(go.transform, CircleTex(), disc, 0.5f, new Color(0f, 0f, 0f, 0.5f));   // dark translucent circle

        var iconTex = _iconCache.Get(spec.IconPng);
        if (iconTex != null)
            AddRawImage(go.transform, iconTex, disc * 0.58f, 0.5f, Color.white);           // white glyph, centred
        else
        {
            var glyph = Glyph(spec.IconKey);
            if (glyph != null) AddTextRegion(go.transform, glyph, Vector2.zero, Vector2.one, 24);
        }

        var btn = go.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;   // InputSystem doesn't dispatch hover → no auto-tint
        btn.onClick.AddListener((UnityAction)(() => { SafeInvoke(spec.OnClick); ClearRailSelection(); }));

        // The 'layout' container has a VerticalLayoutGroup that drives child positions — so DON'T set
        // anchoredPosition (it would be overridden). Just sit right after the template so we land in the
        // button column as a true sibling (same active container, same coordinate space, same layout driver).
        if (template.parent != null) go.transform.SetSiblingIndex(template.GetSiblingIndex() + 1);
        _log.Info($"[uGUI] built login-sidebar button '{spec.Label}' under '{template.parent?.name}'");
        return go;
    }
}
