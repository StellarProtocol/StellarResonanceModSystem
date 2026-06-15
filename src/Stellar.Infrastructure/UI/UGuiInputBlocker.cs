using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// A Stellar-owned, transparent, full-screen uGUI raycast blocker. When active it
/// sits above the game UI (very high <c>sortingOrder</c>) and absorbs all uGUI
/// pointer raycasts, so clicks over a Stellar IMGUI window don't reach the game UI
/// beneath — WITHOUT touching the game's EventSystem or InputSystem (the
/// EventSystem-toggling approach NPE-flooded this InputSystem game). It only adds
/// Stellar-owned GameObjects, so it cannot break game input the way that did.
///
/// Created lazily on the Unity main thread (from Update, not OnGUI). A fully
/// transparent Image still raycasts because <c>raycastTarget</c> is true and
/// alpha-hit-testing is off — same fact used by the Phase 9d injected button.
/// </summary>
internal sealed class UGuiInputBlocker
{
    private const int TopSortingOrder = 32760; // just under short.MaxValue; above game canvases

    private GameObject? _root;
    private bool _failed;

    public void SetActive(bool on)
    {
        if (on && _root == null && !_failed) Create();
        if (_root != null && _root.activeSelf != on) _root.SetActive(on);
    }

    private void Create()
    {
        try
        {
            var go = new GameObject("StellarInputBlocker") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = TopSortingOrder;
            go.AddComponent<GraphicRaycaster>();

            var fill = new GameObject("Fill");
            fill.transform.SetParent(go.transform, worldPositionStays: false);
            var img = fill.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f); // fully transparent…
            img.raycastTarget = true;              // …but still absorbs raycasts
            var rt = img.rectTransform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

            go.SetActive(false);
            _root = go;
        }
        catch { _failed = true; _root = null; }
    }

    public void Destroy()
    {
        if (_root != null) { Object.Destroy(_root); _root = null; }
    }
}
