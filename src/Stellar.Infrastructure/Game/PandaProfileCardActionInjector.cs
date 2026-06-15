using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Injects Stellar-contributed buttons into the game's OWN native profile card
/// (<c>idcard_popup_pc(Clone)</c>) action bar. Each <see cref="ProfileCardActionSpec"/> a plugin
/// registers via <see cref="IProfileCardActions"/> becomes one styled button; clicking it resolves
/// the carded player's <see cref="EntityId"/> and invokes the spec's <c>OnClick</c>. (The
/// EntityInspector registers an "Inspect" action this way; the injector itself is generic.) The card
/// is a transient popup — present only while open — so this polls each tick (<see cref="Tick"/>),
/// injects the current action set once per card-open, and re-injects on the next open (the buttons
/// die with the card).
///
/// <para><b>Per-action injection.</b> On each card-open the injector reads
/// <see cref="IProfileCardActionSource.Actions"/> and BUILDS one button per action (in registration
/// order, each <see cref="Transform.SetAsLastSibling"/>) from scratch — a GameObject with a
/// fully-transparent raycastable <c>Image</c> click surface, an icon (the spec's <c>IconPng</c>
/// rasterised to a <c>Texture2D</c> via <see cref="UI.PluginIconCache"/>, drawn by a <c>RawImage</c>;
/// label-only when <c>IconPng</c> is null), and a TMP label below — parented under the action-bar
/// scrollview's <c>layout_interactive</c> row. This icon-over-label, no-box layout matches the game's
/// own action buttons (Personal Space / Message / Visit / …). Each object is named
/// <c>stellar_action_&lt;Id&gt;</c>; a guard (that name already present) prevents double-inject. Sizing
/// mirrors a sibling <c>&lt;id&gt;/btn_idcard</c> so the buttons fit the action row, and a
/// <c>LayoutElement</c> pins each width/height when the row is driven by a layout group.</para>
///
/// <para><b>Placement = scrollview (renders reliably).</b> The action bar is a game-managed list whose
/// ORDER varies per open, but that reorder is purely COSMETIC: each button's hit-test probes its own
/// live rect, so it never assumes a fixed slot. Re-parenting out of the scrollview failed to render
/// in-world, so the scrollview placement is retained.</para>
///
/// <para><b>Click detection is MANUAL, not via <c>Button.onClick</c>.</b> The idcard popup canvas
/// (<c>UILayerFunc</c>) does not dispatch Unity EventSystem clicks to our injected children (the
/// game's own action buttons are <c>Panda.ZUi.ZButton</c>s driven by a different path), so
/// <see cref="Tick"/> edge-tracks <c>Input.GetMouseButton(0)</c> and hit-tests each injected button's
/// actual <c>RectTransform</c> (mirroring <c>WindowInteractionTicker</c>), resolving the canvas camera
/// from the ROOT canvas (see <see cref="ResolveCanvasCamera"/>).</para>
///
/// <para><b>charId</b> is read at click time straight from the open <c>idcard</c> view via the Lua
/// bridge: <c>Z.UIMgr:GetView("idcard").cardId_</c> (the view stores <c>self.cardId_ = self.viewData.cardId</c>,
/// and <c>cardId == charId</c>). The player <see cref="EntityId"/> is <c>(charId &lt;&lt; 16) | 640</c>.</para>
///
/// <para><b>Threading:</b> the Lua VM and Unity scene graph are main-thread-only; the callers
/// (<see cref="Tick"/> + the button click) are already on the main thread. Any Lua-bridge resolution
/// failure logs once and disables only the click read — the buttons still inject.</para>
/// </summary>
internal sealed partial class PandaProfileCardActionInjector : IDisposable
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string ChunkName = "stellar.idcard";
    private const string CardIdGlobal = "__stellar_idcard_charid";
    private const string CardRootPath = "zuiroot/UILayerFunc/idcard_popup_pc(Clone)";
    private const string ActionBarSubPath =
        "anim/node_press/img_personal/scrollview_interactive/viewport/layout_interactive";
    private const string ButtonNamePrefix = "stellar_action_";
    private const string SiblingButtonChild = "btn_idcard";
    private static readonly Vector2 FallbackSize = new(96f, 96f);

    // Rendered icon footprint (px). The spec PNG is rasterised by PluginIconCache (mip + trilinear).
    private const float IconPx = 44f;

    private readonly IGameTypeRegistry _types;
    private readonly IProfileCardActionSource _actions;
    private readonly IPluginLog _log;
    // Rasterises each action's IconPng → Texture2D (mip/trilinear/HideAndDontSave). Same cache the rail
    // button + launcher menu use; keyed by byte[] reference, survives UnloadUnusedAssets, reloads if collected.
    private readonly UI.PluginIconCache _icons = new();

    // True while a card is open AND we've injected the current action set into it. Reset when the card
    // GameObject goes away, so the next open re-injects (the buttons die with the card).
    private bool _injectedThisOpen;

    // The injected buttons (one per action), cached at inject time so the per-tick manual hit-test
    // doesn't re-Find them. Each entry pairs the spec with its live GameObject/RectTransform. Cleared on close.
    private readonly List<InjectedAction> _injected = new();

    private sealed class InjectedAction
    {
        public ProfileCardActionSpec Spec = null!;
        public GameObject Go = null!;
        public RectTransform Rt = null!;
    }

    // Lua bridge (lazy + cached). Resolution failure disables only the click-time charId read.
    private bool _luaResolved;
    private bool _luaFailLogged;
    private MethodInfo? _mainStateGetter;
    private MethodInfo? _doString;
    private MethodInfo? _luaGetGlobal;
    private MethodInfo? _luaToInteger;
    private MethodInfo? _luaPop;
    private bool _wasMouseDown;

    public PandaProfileCardActionInjector(IGameTypeRegistry types, IProfileCardActionSource actions, IPluginLog log)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Per-tick poll (from the framework Update). Detects the open profile card and injects the
    /// registered action buttons once per open; cheap no-op when no card is open or already injected.</summary>
    public void Tick()
    {
        var cardRoot = GameObject.Find(CardRootPath);
        if (cardRoot == null)
        {
            _injectedThisOpen = false;   // card closed (or never open) — arm for the next open
            _injected.Clear();
            return;
        }

        if (!_injectedThisOpen)
        {
            var layout = cardRoot.transform.Find(ActionBarSubPath);
            if (layout == null) return;      // card mid-build — retry next tick
            if (TryInjectAll(layout)) _injectedThisOpen = true;
        }

        // Pin each button to a STABLE position every tick. The game builds/orders its action list once per
        // open AFTER our inject, so a one-time SetAsLastSibling left us in a different slot each open. Re-
        // asserting last-sibling each tick (in registration order) settles them consistently at the right.
        for (var i = 0; i < _injected.Count; i++) _injected[i].Rt.SetAsLastSibling();

        DetectManualClick();
    }

    // Inject the full registered action set under the action row. Returns false (retry next tick) until a
    // sibling button exists to read geometry/label style from, OR true once injected (possibly zero actions).
    private bool TryInjectAll(Transform layout)
    {
        var actions = _actions.Actions;
        if (actions.Count == 0) return true;   // nothing to inject; armed-this-open so we don't re-scan each tick

        var sibling = FindSiblingButton(layout);
        if (sibling == null) return false;     // row mid-build — retry next tick

        var size = SiblingSize(sibling);
        _injected.Clear();
        foreach (var spec in actions)
        {
            // Idempotent: a previous tick may already have built this button under the card.
            var name = ButtonNamePrefix + spec.Id;
            var existing = layout.Find(name);
            var go = existing != null ? existing.gameObject : BuildButton(layout, size, sibling, spec, name);
            var rt = go.transform.TryCast<RectTransform>();
            if (rt != null) _injected.Add(new InjectedAction { Spec = spec, Go = go, Rt = rt });
        }
        DiagInjected(_injected.Count);
        return true;
    }

    // A live sibling action button ("<id>/btn_idcard") — used purely as a geometry + label-style template
    // (we don't clone it). Skip our own buttons. Each native action is keyed by a numeric id.
    private static Transform? FindSiblingButton(Transform layout)
    {
        for (var i = 0; i < layout.childCount; i++)
        {
            var child = layout.GetChild(i);
            if (child.name.StartsWith(ButtonNamePrefix, StringComparison.Ordinal)) continue;
            var btn = child.Find(SiblingButtonChild);
            if (btn != null && btn.GetComponent<UnityEngine.UI.Button>() != null) return btn;
        }
        return null;
    }

    // Match the sibling row item's footprint. Prefer the action CELL (sibling's parent — what the layout
    // group sizes) so our LayoutElement reserves the same width; fall back to the button, then a default.
    private static Vector2 SiblingSize(Transform sibling)
    {
        var cellRt = sibling.parent != null ? sibling.parent.TryCast<RectTransform>() : null;
        if (cellRt != null && cellRt.sizeDelta.x > 1f && cellRt.sizeDelta.y > 1f) return cellRt.sizeDelta;
        var btnRt = sibling.TryCast<RectTransform>();
        if (btnRt != null && btnRt.sizeDelta.x > 1f && btnRt.sizeDelta.y > 1f) return btnRt.sizeDelta;
        return FallbackSize;
    }

    // Left-mouse-down hit-test against EACH injected button's ACTUAL rect. The action bar is a game-managed
    // list whose ORDER varies per open, so hit-testing each button's own rect (rather than a fixed slot) is
    // correct. We edge-track the HELD state ourselves (GetMouseButton level) rather than GetMouseButtonDown:
    // this Tick runs on the Panda Game.Update loop, which doesn't align with the Unity frame where the
    // down-edge is true, so GetMouseButtonDown was missed on most clicks (the "needs 10-15 clicks" symptom).
    private void DetectManualClick()
    {
        if (_injected.Count == 0) { _wasMouseDown = false; return; }

        var down = Input.GetMouseButton(0);
        var pressed = down && !_wasMouseDown;
        _wasMouseDown = down;
        if (!pressed) return;

        for (var i = 0; i < _injected.Count; i++)
        {
            var hit = _injected[i];
            if (hit.Go == null || hit.Rt == null || !hit.Go.activeInHierarchy) continue;
            var cam = ResolveCanvasCamera(hit.Go);
            if (!RectTransformUtility.RectangleContainsScreenPoint(hit.Rt, Input.mousePosition, cam)) continue;
            InvokeAction(hit.Spec);
            return;   // a single click resolves to one button
        }
    }

    // The camera RectangleContainsScreenPoint needs for a Camera/World-space canvas; null for a
    // ScreenSpaceOverlay canvas. Resolve from the ROOT canvas, not the nearest one: a nested sub-canvas can
    // report a null/wrong worldCamera while the rootCanvas carries the correct one.
    private static Camera? ResolveCanvasCamera(GameObject go)
    {
        var canvas = go.GetComponentInParent<Canvas>()?.rootCanvas;
        return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
    }

    private void InvokeAction(ProfileCardActionSpec spec)
    {
        try
        {
            var charId = ReadOpenCardCharId();
            if (charId <= 0) return;   // unreadable open-card charId — gated DiagCardIdReadFailed already noted it
            DiagHitTestClick(spec.Id, charId);
            spec.OnClick(new EntityId((charId << 16) | 640L));
        }
        catch (Exception ex)
        {
            _log.Error($"[IdCard] action '{spec.Id}' click threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Destroys the rasterised action-icon textures (framework reload / teardown).</summary>
    public void Dispose() => _icons.Dispose();

    // ── Lua bridge: read the open idcard view's cardId_ (== charId) ──────────────────────────────────

    // Read the open idcard view's cardId_ (== charId) via the Lua bridge: stash it in a global, pull it
    // back. Returns 0 if the bridge is unresolved or no idcard view is open.
    private long ReadOpenCardCharId()
    {
        if (!TryResolveLua() || _luaGetGlobal is null || _luaToInteger is null || _luaPop is null) return 0;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnLuaOnce("LuaState.mainState was null"); return 0; }

        _doString!.Invoke(state, new object[] { BuildReadChunk(), ChunkName });
        var id = ReadLuaGlobalInt(state, CardIdGlobal);   // numeric read — LuaToInteger, no Il2Cpp boxing
        if (id > 0) return id;
        DiagCardIdReadFailed();   // click landed but the open-card cardId came back 0
        return 0;
    }

    // LuaGetGlobal pushes the value; LuaToInteger reads it as a CLR long directly (ToVariant boxed it as an
    // opaque Il2CppSystem.Object whose .ToString() gave the type name, not the value).
    private long ReadLuaGlobalInt(object state, string name)
    {
        _luaGetGlobal!.Invoke(state, new object[] { name });
        var result = _luaToInteger!.Invoke(state, new object[] { -1 });
        _luaPop!.Invoke(state, new object[] { 1 });
        return result is null ? 0 : Convert.ToInt64(result);
    }

    // GetView("idcard").cardId_ (== charId, set in idcard_view.lua). Stashed as a NUMBER (read back with
    // LuaToInteger). pcall-guarded so a Lua error can't escape into the host.
    internal static string BuildReadChunk() =>
        "rawset(_G, '" + CardIdGlobal + "', 0)\n" +
        "pcall(function()\n" +
        "  local v = (Z.UIMgr):GetView('idcard')\n" +
        "  if not v then return end\n" +
        "  if v.cardId_ == nil then return end\n" +
        "  rawset(_G, '" + CardIdGlobal + "', v.cardId_)\n" +
        "end)";

    private bool TryResolveLua()
    {
        if (_luaResolved) return true;
        var luaStateType = _types.FindType("ZLuaFramework.LuaState") ?? _types.FindType("LuaInterface.LuaState");
        if (luaStateType is null) return false;   // hot-update not loaded yet — retry on next click

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        _doString = FindMethod(luaStateType, "DoString", typeof(string), typeof(string));
        _luaGetGlobal = FindMethod(luaStateType, "LuaGetGlobal", typeof(string));
        _luaToInteger = FindMethod(luaStateType, "LuaToInteger", typeof(int));
        _luaPop = FindMethod(luaStateType, "LuaPop", typeof(int));
        if (_mainStateGetter is null || _doString is null
            || _luaGetGlobal is null || _luaToInteger is null || _luaPop is null)
        {
            WarnLuaOnce("LuaState bridge (mainState/DoString/LuaGetGlobal/LuaToInteger/LuaPop) not fully found");
            return false;
        }

        _luaResolved = true;
        DiagLuaResolved();
        return true;
    }

    private static MethodInfo? FindMethod(Type type, string name, params Type[] paramTypes)
    {
        foreach (var m in type.GetMethods(AnyInstance))
        {
            if (m.Name != name || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != paramTypes.Length) continue;
            var match = true;
            for (var i = 0; i < ps.Length; i++)
                if (ps[i].ParameterType != paramTypes[i]) { match = false; break; }
            if (match) return m;
        }
        return null;
    }

    private void WarnLuaOnce(string msg)
    {
        if (_luaFailLogged) return;
        _luaFailLogged = true;
        _log.Warning($"[IdCard] {msg}");
    }
}
