using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Unity;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Prepares the portrait model and hands it to <see cref="PortraitCmdRenderer"/>, which draws it into a render
/// texture with our own CommandBuffer. (The game's own ZModel2RT render feature can't be driven standalone, and
/// registering with it leaked into the world render — see HANDOFF §5.) This host: creates the RT, collects the
/// model's renderers through the game's <c>ZModel2RTData.UpdateRenderersByModel</c> (we Rent a data object ONLY
/// for that — we never register it with the feature), faces the model at the camera and LOD-locks it, then hands
/// the renderer set + framing to the cmd renderer. The model is lit by the current scene (the custom SRP feeds
/// the creature shaders via a constant buffer loose-global overrides can't touch).
/// </summary>
internal sealed partial class PortraitModelHost
{
    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;

    private RenderTexture? _rt;
    private object? _data;             // ZModel2RTData — held only so Texture knows a model is active
    private object? _model;            // the live ZModel proxy — for body-anchor framing + facing over the settle window
    private Type? _dataType, _zmodelType;
    private PortraitCmdRenderer? _cmdRenderer;
    private bool _created, _failed;
    private int _settleFrames;         // counts the frames after a model is prepared (framing/facing re-asserted)
    private const int SettleWindow = 90;

    public PortraitModelHost(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The render texture the model is drawn into, or null until a model is prepared.</summary>
    public object? Texture
    {
        get
        {
            if (_data is null) return null;
            SettleTick();           // re-derive body framing + re-assert facing for the first frames after load
            DiagTexturePoll();      // self-gated on StellarDiagnostics.IsEnabled (see .Diagnostics.cs)
            return _rt;
        }
    }

    // For the first SettleWindow frames after a model is prepared, re-derive framing from the skeleton's head/foot
    // anchors (which ignore the weapon's pose) and re-assert facing (the game's placement overrides it right after
    // load → first-open backside). Driven from the per-frame Texture poll while the inspector is shown.
    private void SettleTick()
    {
        if (_model is null || _settleFrames >= SettleWindow) return;
        if (_settleFrames < 6) PlaceModelFacingCamera(_model);   // re-assert facing early
        PushBodyFraming(_model);
        _settleFrames++;
    }

    // Frame the BODY from head/foot anchors (skeleton points — the weapon never affects them).
    private void PushBodyFraming(object model)
    {
        var foot = PortraitReflect.Invoke(model, "GetAttrGoPosition") is Vector3 f ? f : Vector3.zero;
        var head = PortraitReflect.Invoke(model, "GetHeadPosition") is Vector3 h ? h : foot + new Vector3(0f, 1.6f, 0f);
        var bodyH = Mathf.Max(0.6f, head.y - foot.y);
        var bottom = foot.y;
        var top = head.y + bodyH * 0.18f;                        // headroom so the hat isn't clipped
        var target = new Vector3(foot.x, (top + bottom) * 0.5f, foot.z);
        var orthoHalf = (top - bottom) * 0.5f * 1.06f;
        _cmdRenderer?.SetFraming(target, orthoHalf);
    }

    /// <summary>Resolve the game types + create the render texture (once). False if the types aren't loaded yet.</summary>
    // HideAndDontSave is REQUIRED: a plain runtime RenderTexture is reclaimed by Resources.UnloadUnusedAssets
    // on a scene change, leaving _rt Unity-destroyed (== null) and the portrait blank after city→guild→city
    // (user-flagged 2026-06-13; same class as the plugin-texture hide-flags fix). DontDestroyOnLoad keeps the
    // managed handle across loads too.
    private static RenderTexture CreateRt(int w, int h)
    {
        // MIPMAPS + Trilinear are the real smoothness lever: the RawImage shrinks this 3×-supersampled RT
        // to the pane, and plain bilinear minification only reads a 2×2 block — undersampling the extra
        // detail, so edges stayed rough (user-flagged 2026-06-13). With mips the GPU samples the correct
        // pre-averaged level → a proper box downsample. Mips are incompatible with MSAA, so antiAliasing=1
        // (the 3× supersample provides the edge AA; the cmd renderer regenerates mips after each draw).
        var rt = new RenderTexture(w, h, 24)
        {
            name = "StellarPortraitRT", antiAliasing = 1, useMipMap = true, autoGenerateMips = false,
            filterMode = FilterMode.Trilinear, hideFlags = HideFlags.HideAndDontSave,
        };
        rt.Create();
        UnityEngine.Object.DontDestroyOnLoad(rt);
        return rt;
    }

    public bool EnsureCreated()
    {
        if (_created) return _dataType != null;
        _created = true;
        _dataType = _types.FindType("Panda.ZUi.ZModel2RTData");
        _zmodelType = _types.FindType("Panda.ZGame.ZModel");
        if (_dataType is null || _zmodelType is null)
        {
            Warn($"types missing: data={_dataType != null} zmodel={_zmodelType != null}");
            return false;
        }
        // Tall aspect (~0.42) near the portrait pane's shape; SetViewport later resizes it to the live pane.
        _rt = CreateRt(440, 1040);
        _log.Info($"[Portrait] host created (rt={_rt.width}x{_rt.height})");
        return true;
    }

    /// <summary>Prepare the model once it has streamed in. Returns false while still loading — call again next frame.</summary>
    public bool AssignModel(object model)
    {
        if (_failed || _dataType is null || _zmodelType is null) return true;
        var ptr = (model as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
        if (ptr == IntPtr.Zero) { Warn($"not an Il2Cpp object ({model.GetType().Name})"); _failed = true; return true; }
        var typed = Activator.CreateInstance(_zmodelType, ptr)!;
        if (PortraitReflect.Get(typed, "Loaded") is false) return false;
        try { PrepareModel(typed); }
        catch (Exception ex) { Warn($"PrepareModel threw: {PortraitReflect.Unwrap(ex)}"); _failed = true; }
        return true;
    }

    // Collect the model's renderers (via the game's own UpdateRenderersByModel), face + LOD-lock it, hand to cmd.
    private void PrepareModel(object model)
    {
        var data = PortraitReflect.InvokeStatic(_dataType!, "Rent")!;
        PortraitReflect.Set(data, "Model", model);
        PlaceModelFacingCamera(model);
        LockLod(model);
        PortraitReflect.Invoke(data, "UpdateRenderersByModel");
        var renders = PortraitReflect.Get(data, "Renders");
        _log.Info($"[Portrait] renderers collected: {(renders is null ? "?" : PortraitReflect.Get(renders, "Count"))}");
        _data = data;
        SetupCmdRenderer(model, renders);
        DiagAfterPrepare(renders);   // self-gated diagnostics (renderer-state dump + render-pass state)
    }

    private void SetupCmdRenderer(object model, object? renders)
    {
        if (renders is null) return;
        // Self-heal: a scene change can still reclaim the RT (e.g. before the hide-flags landed, or a forced
        // unload) — recreate it so a re-inspection after returning to a scene isn't blank. `_rt == null` is
        // true both for never-created and Unity-destroyed.
        if (_rt == null) _rt = CreateRt(440, 1040);
        var list = ExtractRenderers(renders);
        if (list.Count == 0) { Warn("no renderers extracted for cmd draw"); return; }
        var root = list[0].transform.root;     // the model root — cmd renderer rescans it for streamed clothing
        _model = model;
        _settleFrames = 0;
        _cmdRenderer ??= PortraitCmdRenderer.Create(m => _log.Info(m));
        _cmdRenderer.SetActive(true);
        _cmdRenderer.SetTargets(_rt, root);
        SettleTick();                          // initial framing + facing before the first Texture poll
    }

    private static List<Renderer> ExtractRenderers(object renders)
    {
        var list = new List<Renderer>();
        var count = PortraitReflect.Get(renders, "Count") is int c ? c : 0;
        for (var i = 0; i < count; i++)
        {
            object? item;
            try { item = PortraitReflect.Invoke(renders, "get_Item", i) ?? PortraitReflect.Invoke(renders, "Get", i); }
            catch { continue; }
            var r = item as Renderer ?? (item as Il2CppObjectBase)?.TryCast<Renderer>();
            if (r != null) list.Add(r);
        }
        return list;
    }

    // Face the model at the camera. The cmd renderer places the camera at world -Z of the model; this mesh's
    // FRONT is along its local -Z, so yaw 0 (not 180) turns the front toward the camera (180 showed the back).
    private void PlaceModelFacingCamera(object model)
        => PortraitReflect.Invoke(model, "SetAttrGoRotation", Quaternion.Euler(0f, 0f, 0f));

    // Cap the model at the UI LOD (EModelLod.EUi = 0) — full detail. NOTE: this cap alone does NOT stop the
    // weapon "resizing" jitter; the actual fix is PortraitCmdRenderer.EnforceLod re-forcing LOD0 on the Unity
    // LODGroups every frame (verified in-world — elaborate multi-form weapons were the visible case). Kept
    // because it sets the intended detail level and is the natural place if a game-side LOD lever is needed.
    private void LockLod(object model)
    {
        try
        {
            var lodType = _types.FindType("Panda.ZGame.EModelLod");
            if (lodType != null) PortraitReflect.Invoke(model, "SetLodLimit", Enum.ToObject(lodType, 0));   // EUi cap
            else Warn("EModelLod not found — LOD limit not set");
        }
        catch (Exception ex) { Warn($"SetLodLimit failed: {PortraitReflect.Unwrap(ex)}"); }
    }

    /// <summary>Stop drawing the current model (the Lua bridge recycles the model itself).</summary>
    public void ClearModel()
    {
        _data = null;
        _model = null;
        _failed = false;   // each new inspection retries cleanly — a one-off PrepareModel throw must not blank the portrait for the rest of the process
        _cmdRenderer?.ClearTargets();
    }

    /// <summary>Show/hide the portrait renderer.</summary>
    public void SetVisible(bool on) => _cmdRenderer?.SetActive(on);

    /// <summary>Tuning hook (no-op — framing is computed from the model anchors).</summary>
    public void ApplyTuning() { }

    /// <summary>Orbit the portrait camera (horizontal drag spins, vertical tilts).</summary>
    public void Orbit(float dx, float dy) => _cmdRenderer?.Orbit(dx, dy);

    /// <summary>Zoom the portrait camera (positive = closer).</summary>
    public void Zoom(float delta) => _cmdRenderer?.Zoom(delta);

    /// <summary>Pan the portrait camera (shift+drag).</summary>
    public void Pan(float dx, float dy) => _cmdRenderer?.Pan(dx, dy);

    // Render the RT at 3× the pane size, then let the RawImage downscale it: the GPU's bilinear
    // minification antialiases every silhouette edge. This is pure spatial supersampling — it can't match
    // the world view's TEMPORAL AA (TAA/DLSS/FSR) + post-process sharpen, which our isolated single-frame
    // CommandBuffer render bypasses, so the portrait reads slightly softer by design (user Q 2026-06-13).
    // Cap is per-dimension; raised to 4096 so a tall pane × 3 doesn't clamp one axis and distort the aspect.
    private const int Supersample = 3;
    private const int RtMaxDim = 4096;

    /// <summary>Resize the render texture to match the display pane (debounced) so the model fills it with no
    /// letterbox/stretch; the cmd renderer reads the new aspect from the RT automatically.</summary>
    public void SetViewport(int width, int height)
    {
        if (_rt == null) return;
        var w = Mathf.Clamp(width * Supersample, 64, RtMaxDim);
        var h = Mathf.Clamp(height * Supersample, 64, RtMaxDim);
        if (Mathf.Abs(_rt.width - w) < 8 && Mathf.Abs(_rt.height - h) < 8) return;   // debounce small/no changes
        var old = _rt;
        _rt = CreateRt(w, h);
        // Clear the new RT to transparent so the gap before the cmd renderer's first draw shows the dark backdrop
        // Image (not the uninitialized-white texture → the resize flicker).
        var prevActive = RenderTexture.active;
        RenderTexture.active = _rt;
        GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
        RenderTexture.active = prevActive;
        _cmdRenderer?.SetRenderTexture(_rt);
        if (old != null) { old.Release(); UnityEngine.Object.Destroy(old); }
    }

    private void Warn(string msg) => _log.Warning($"[Portrait] {msg}");
}
