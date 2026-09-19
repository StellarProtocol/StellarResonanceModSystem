using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Unity;
using UnityEngine;
using UnityEngine.Rendering;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Prepares the portrait model and hands it to <see cref="PortraitCmdRenderer"/>, which draws it into a render
/// texture with our own CommandBuffer. (The game's own ZModel2RT render feature can't be driven standalone, and
/// registering with it leaked into the world render — see HANDOFF §5.) This host: creates the RT, collects the
/// model's renderers through the game's <c>ZModel2RTData.UpdateRenderersByModel</c> (we Rent a data object ONLY
/// for that — we never register it with the feature), faces the model at the camera and LOD-locks it, then hands
/// the renderer set + framing to the cmd renderer. The portrait is creature-lit: it hands the cmd renderer a light
/// injector (<see cref="ResolveLightInjector"/>) that writes the baked EHighNoon creature-light globals into our
/// draw, so the portrait's lighting is independent of the surrounding scene (dark caves / night).
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
            // Recompute the weapon exclusion on a coarse throttle while the portrait is shown: the WEAPON STREAMS
            // IN AFTER THE BODY (async), so the one-shot call at setup runs before the weapon renderer exists and
            // misses it. Not limited to the settle window — weapon load timing varies. Cheap (a small array walk).
            if (++_weaponExclFrame % 12 == 0) RefreshWeaponExclusion();
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

    // --- Weapon exclusion (hide the weapon in the PORTRAIT only) ----------------------------------------------
    // Our portrait is drawn by a custom CommandBuffer (PortraitCmdRenderer.DrawModel → _cmd.DrawRenderer), which
    // IGNORES Unity render layers — so the game's SetRenderLayerMaskByRenderType / layer-mask hide does NOT reach
    // our draw. The reliable lever is to EXCLUDE the weapon renderers from our draw set. The weapon is NOT a mount
    // part (slots 1024/1025 confirmed empty) — it's a plain child under the model root whose renderer GameObjects
    // are named ch_wp_*(Clone) (ch_wp_ = character-weapon prefix), parented to back attach points. The in-game
    // diagnostic confirmed these are the ONLY renderers under the root NOT in the body set (body/face/hair/headwear
    // use ch_f_/ch_c_ prefixes), so we exclude by GameObject-name prefix. Re-run on a throttle: the weapon streams
    // in AFTER the body, so a one-shot at setup finds 0 — the ~12-frame Texture-poll re-assert catches it later.
    private int  _weaponExclFrame;              // per-frame throttle counter for the Texture-poll recompute
    private bool _weaponExclLogged;             // log the success line once per model
    private bool _showWeapon;                   // default false = weapon hidden (matches prior always-hide behavior)

    /// <summary>When false (default) the portrait hides the weapon; true shows it. Applies immediately to the live
    /// model (re-asserts the exclusion set); safe to set with no model loaded — RefreshWeaponExclusion guards.</summary>
    public bool ShowWeapon
    {
        get => _showWeapon;
        set { if (_showWeapon == value) return; _showWeapon = value; RefreshWeaponExclusion(); }
    }

    // Throttle/entry: guard, collect the weapon renderers, push the exclusion set, log once per model.
    private void RefreshWeaponExclusion()
    {
        if (_model is null || _cmdRenderer is null) return;
        // Show-weapon: exclude nothing (draw the whole model incl. the ch_wp_* renderers) and stop re-asserting.
        if (_showWeapon) { _cmdRenderer.SetExcludedRenderers(null); return; }
        try
        {
            if (!TryCollectWeaponRenderers(out var set)) return;   // model root not ready yet (streaming)
            _cmdRenderer.SetExcludedRenderers(set);
            // Log ONCE per model on the first frame we actually hid something (set.Count > 0), so the weapon-still-
            // streaming early frames (count 0) don't latch the flag and suppress the real success line.
            if (set.Count > 0 && !_weaponExclLogged)
            {
                _log.Info($"[Portrait] weapon-exclude: hid {set.Count} weapon renderer(s) by name (ch_wp_*)");
                _weaponExclLogged = true;
            }
        }
        catch (Exception ex)
        {
            if (!_weaponExclLogged) { Warn($"weapon-exclude failed: {PortraitReflect.Unwrap(ex)}"); _weaponExclLogged = true; }
        }
    }

    // Collect the weapon renderers by GameObject-name prefix (ch_wp_*) under the model root and drop them from our
    // draw set. Returns false when the model root isn't resolvable yet (renderers still streaming); an empty set is
    // NOT a failure (weapon still loading) — the ~12-frame Texture-poll re-assert catches it once the weapon loads.
    private bool TryCollectWeaponRenderers(out HashSet<Renderer> set)
    {
        set = new HashSet<Renderer>();
        var root = BodyRoot();                 // reuse the helper (transform.root of _data.Renders[0]); in .Diagnostics.cs, same partial class
        if (root == null) return false;        // renderers not ready yet (streaming)
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            if (r != null && r.gameObject.name.StartsWith("ch_wp_", System.StringComparison.OrdinalIgnoreCase))
                set.Add(r);
        return true;
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
        var injector = ResolveLightInjector();
        _cmdRenderer.SetLightInjector(injector);   // creature-light the portrait (EHighNoon into our cmd) — null if unresolved → scene-lit
        // Pair the after-draw scene re-push with the injector: SwitchLightType's shader globals persist past our cmd,
        // so we must END the cmd on scene lighting or the game's world creature pass inherits our portrait profile.
        // null when the injector is unresolved (nothing was overridden → nothing to restore).
        _cmdRenderer.SetLightRestorer(injector is null ? null : RestoreSceneLight);
        RefreshWeaponExclusion();              // drop the weapon from the portrait draw (re-run per frame — weapon streams in late)
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
        _cmdRenderer?.SetExcludedRenderers(null);   // a re-inspection starts with nothing excluded
        _weaponExclLogged = false;                  // re-log the exclude count once on the next model
        _lightBaseLogged = false;                   // re-capture the scene light baseline once on the next model
    }

    /// <summary>Show/hide the portrait renderer.</summary>
    public void SetVisible(bool on) => _cmdRenderer?.SetActive(on);

    // --- Environment-independent portrait light (creature light written into OUR OWN command buffer) --------
    // The game lights preview models with a set of global "creature RT light" shader params, pushed each frame
    // by its OWN pass (ZModelSnapshotRenderPass). That pass never touches our custom-CommandBuffer draw, so the
    // portrait tracked the surrounding scene (dark in caves / at night). Experiment A (the ZModelGlobalColor
    // global-preset flip) confirmed no-op for us — it drives that separate pass, not our cmd.
    //
    // The guaranteed fix: write the baked creature-light globals INTO OUR cmd right before we draw, via the
    // game's CommandBuffer variant Bokura.Rendering.ZModel2RTLight.SwitchLightType(cmd, EHighNoon, cameraPos)
    // (dump.cs:1131107). No manual packing, no dependency on the game's pass — the method writes the correct
    // per-preset values into whatever CommandBuffer we hand it, and nothing interleaves before our DrawModel.
    // Resolved HERE (this host has the type registry); the injector delegate is handed to the cmd renderer,
    // which invokes it after its view/proj set and immediately before the draw. Unresolved → null delegate →
    // portrait stays scene-lit (no regression).
    private MethodInfo? _switchLight;   // ZModel2RTLight.SwitchLightType(CommandBuffer, ECreatureRTLightType, Vector4)
    private Type? _lightEnumType;       // the method's ECreatureRTLightType param type — box any preset value from it, no name guess
    private object[]? _lightArgs;       // cached invoke args; indices 0 (cmd) + 2 (cameraPos) mutated per frame, 1 (enum) per-preset
    private bool _lightTried;

    // Light preset: -1 = Scene (skip injection → environment-lit), 0..4 = the baked creature-RT presets
    // (0=EarlyMorning,1=Morning,2=HighNoon,3=Sunset,4=Night). Default 2 (EHighNoon) preserves prior behavior.
    // A live change is picked up next frame by the per-frame InjectLight — no extra refresh needed.
    private int _lightPreset = 2;
    private object? _boxedPreset;       // cached boxing of _lightPreset (avoid re-boxing every frame when unchanged)
    private int _boxedFor = int.MinValue;

    /// <summary>Portrait light preset (-1 = Scene, 0..4 = baked presets). Live — the per-frame injector reads it.</summary>
    public int LightPreset { get => _lightPreset; set => _lightPreset = value; }

    // Resolve the CommandBuffer-variant SwitchLightType once and return the injector, or null if it can't be
    // resolved (portrait stays scene-lit). Robust resolution avoids all enum-name guessing: match the method by
    // {CommandBuffer, enum, Vector4} shape (Length==3, param 1 is an enum) and box EHighNoon(2) from that param's
    // own type — the earlier FindType-by-name of the (nested) enum is exactly what broke Experiment A.
    private Action<CommandBuffer, Vector3>? ResolveLightInjector()
    {
        if (_lightTried) return _switchLight is null ? null : InjectLight;
        _lightTried = true;
        try
        {
            var lightType = _types.FindType("Bokura.Rendering.ZModel2RTLight")
                            ?? _types.FindType("ZModel2RTLight");
            if (lightType is null) { Warn("portrait-light: ZModel2RTLight not found — portrait stays scene-lit"); return null; }

            foreach (var m in lightType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "SwitchLightType") continue;
                var ps = m.GetParameters();
                if (ps.Length != 3 || !ps[1].ParameterType.IsEnum) continue;
                _switchLight = m;
                _lightEnumType = ps[1].ParameterType;   // cache the ECreatureRTLightType — InjectLight boxes any preset from it
                break;
            }
            if (_switchLight is null) { Warn("portrait-light: SwitchLightType(cmd,enum,Vector4) not found — portrait stays scene-lit"); return null; }
            _log.Info("[Portrait] creature-light injector resolved (preset written into our cmd; -1 = scene-lit)");
            return InjectLight;
        }
        catch (Exception ex) { Warn($"ResolveLightInjector threw: {PortraitReflect.Unwrap(ex)}"); return null; }
    }

    // Write the selected creature-light preset globals into OUR command buffer. Called by the cmd renderer just
    // before DrawModel (so nothing interleaves before our draw); camPos = the portrait camera's world position.
    private void InjectLight(CommandBuffer cmd, Vector3 camPos)
    {
        // Scene preset (-1): skip injection entirely so the model stays environment-lit (the game's dynamic look).
        // NOTE: the creature-light shader globals may still hold the LAST injected preset's values from a prior
        // frame — accepted for now (the game's own passes refresh scene lighting each frame). We deliberately do
        // NOT try to actively restore scene lighting here; we simply stop writing our override.
        if (_lightPreset < 0) return;
        try
        {
            // Custom lighting (experiment): SwitchLightType reads STATIC value fields on ZModel2RTLight — the
            // ECreatureRTLightType arg no longer differentiates the look. Capture the scene values ONCE for
            // calibration, then re-assert our profile into those statics (the world volume stomps them each frame),
            // BEFORE the SwitchLightType push below reads them into our cmd. See PortraitModelHost.Lighting.cs.
            LogLightBaseline();               // one-shot — reads the CURRENT (scene) values before we overwrite them
            CaptureWorldGlobals();            // capture the EXACT world creature-light GPU globals BEFORE we stomp them (restored verbatim post-draw)
            SaveLightStatics();               // snapshot the scene values so we can restore them after the push below
            ApplyLightProfile(_lightPreset);  // no-op if the light statics didn't resolve → falls back to prior behavior
            if (_boxedFor != _lightPreset)   // cache the boxed enum so we don't re-box every frame when unchanged
            {
                _boxedPreset = Enum.ToObject(_lightEnumType!, _lightPreset);
                _boxedFor = _lightPreset;
            }
            _lightArgs ??= new object[3];
            _lightArgs[0] = cmd;
            _lightArgs[1] = _boxedPreset!;
            _lightArgs[2] = new Vector4(camPos.x, camPos.y, camPos.z, 1f);
            _switchLight!.Invoke(null, _lightArgs);   // records OUR static values into cmd (captured at record time)
            RestoreLightStatics();            // put scene values back so the game's WORLD pass isn't affected
        }
        catch (Exception ex) { Warn($"InjectLight threw: {PortraitReflect.Unwrap(ex)}"); }
    }

    // Restore the EXACT world creature-light GPU globals into our cmd AFTER the draw. Called by the cmd renderer
    // immediately after DrawModel (so InjectLight has already captured the pre-stomp world values this frame via
    // CaptureWorldGlobals). SwitchLightType's shader globals PERSIST after our cmd executes, so the game's world
    // creature pass would otherwise render with our portrait profile. We now write the CAPTURED world values back
    // VERBATIM (cmd.SetGlobal*) — the net recording is [SwitchLightType=OUR values][Draw][SetGlobal=WORLD values],
    // so the GPU global state ends byte-identical to what the world left. This REPLACES the old SwitchLightType
    // re-push, which repacked via the preview path (not byte-identical → residual tone leaked). No-op in Scene mode
    // (-1) or when nothing was captured (ids unresolved / injector never ran). Never throws from LateUpdate.
    private void RestoreSceneLight(CommandBuffer cmd, Vector3 camPos)
    {
        RestoreWorldGlobals(cmd);   // camPos no longer needed — world values are captured, not recomputed from camera
    }

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
