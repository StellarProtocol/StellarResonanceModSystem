using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Draws a set of character renderers into a <see cref="RenderTexture"/> every frame with an explicit
/// <see cref="CommandBuffer"/> — the core of what the game's <c>ZModelSnapshotRenderPass</c> does
/// (<c>setCameraMatrices</c> + <c>DrawRenderer</c>), but self-contained so it never depends on whether the game's
/// scriptable pass is enqueued. The camera is recomputed each frame from orbit/zoom state so the portrait is an
/// interactive view. Skinned meshes update their pose in <c>LateUpdate</c>, so the draw runs there.
/// </summary>
public sealed class PortraitCmdRenderer : MonoBehaviour
{
    public PortraitCmdRenderer(IntPtr ptr) : base(ptr) { }

    private static bool _registered;

    private const float ViewDist = 6f;              // fixed camera distance (ortho → distance doesn't affect scale)

    private RenderTexture? _rt;
    private Vector3 _target;
    private float _orthoHalf = 1f;                  // half-height of the orthographic view (≈ half the body height)
    private Transform? _root;                       // model root — re-scanned for late-streamed clothing meshes
    private readonly List<Renderer> _renderers = new();
    private readonly List<LODGroup> _lodGroups = new();   // cached on Rescan; LOD0 re-forced every frame (no per-frame GetComponentsInChildren)
    private int _rescan;
    private CommandBuffer? _cmd;

    private float _azimuth;      // degrees around the model (0 = front)
    private float _elevation;    // degrees up/down
    private float _zoom = 1f;     // >1 = closer
    private Vector3 _panOffset;   // look-at offset accumulated from shift+drag (camera-relative)
    private int _frame;
    private readonly List<(Renderer R, Material M, int Sub)> _drawItems = new();
    private static readonly Comparison<(Renderer R, Material M, int Sub)> ByQueue =
        (a, b) => a.M.renderQueue.CompareTo(b.M.renderQueue);
    private static readonly ShaderTagId LightModeTag = new("LightMode");
    private Action<string>? _log;

    /// <summary>Create the renderer on a hidden, dont-destroy GameObject.</summary>
    public static PortraitCmdRenderer Create(Action<string> log)
    {
        if (!_registered) { try { ClassInjector.RegisterTypeInIl2Cpp<PortraitCmdRenderer>(); } catch { /* already */ } _registered = true; }
        var go = new GameObject("StellarPortraitCmdRenderer") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(go);
        var r = go.AddComponent<PortraitCmdRenderer>();
        r._log = log;
        r._cmd = new CommandBuffer { name = "StellarPortrait" };
        return r;
    }

    /// <summary>Set what to draw: the RT target and the model root (re-scanned for streamed clothing). Framing is
    /// supplied separately via <see cref="SetFraming"/> (the host derives it from the skeleton's head/foot
    /// anchors, which ignore the weapon's pose). Orbit/zoom/pan reset.</summary>
    public void SetTargets(RenderTexture rt, Transform root)
    {
        _rt = rt;
        _root = root;
        _azimuth = 0f; _elevation = 0f; _zoom = 1f; _panOffset = Vector3.zero; _frame = 0; _rescan = 0;
        Rescan();
        _log?.Invoke($"[Portrait] cmd targets: renderers={_renderers.Count}");
    }

    /// <summary>Set the look-at point + orthographic half-height (the host recomputes these over the settle window
    /// from the model's body anchors — head/foot — so the weapon never affects framing).</summary>
    public void SetFraming(Vector3 target, float orthoHalf)
    {
        _target = target;
        _orthoHalf = orthoHalf;
    }

    /// <summary>Swap the render target (when the pane resizes the RT is recreated at the new size).</summary>
    public void SetRenderTexture(RenderTexture rt) => _rt = rt;

    /// <summary>Show/hide the renderer (disabled → its LateUpdate stops drawing).</summary>
    public void SetActive(bool on) { if (this != null) gameObject.SetActive(on); }

    /// <summary>Stop drawing (keeps the GameObject for reuse).</summary>
    public void ClearTargets() { _rt = null; _root = null; _renderers.Clear(); _lodGroups.Clear(); }

    // Draw the passes of a material whose LightMode tag matches the predicate. Outline/shadow/special passes are
    // excluded (drawing them all painted blue garbage). For the color phase, falls back to pass 0 if untagged.
    private void DrawPasses(Renderer r, Material m, int sub, Func<string, bool> want)
    {
        var sh = m.shader;
        var any = false;
        for (var p = 0; p < sh.passCount; p++)
        {
            if (!want(sh.FindPassTagValue(p, LightModeTag).name)) continue;
            _cmd!.DrawRenderer(r, m, sub, p);
            any = true;
        }
        if (!any && want == IsForwardColor) _cmd!.DrawRenderer(r, m, sub, 0);
    }

    private static readonly Func<string, bool> IsForwardColor = lm => lm is
        "UniversalForward" or "UniversalForwardOnly" or "ForwardLit" or "LightweightForward" or "SRPDefaultUnlit";

    private static readonly Func<string, bool> IsDepth = lm => lm is
        "DepthOnly" or "DepthNormals" or "DepthNormalsOnly";


    private void Rescan()
    {
        if (_root == null) return;
        // Cache every LODGroup and lock it to LOD0. ForceLOD(0) once per Rescan was NOT enough: the game
        // re-evaluates the clone's LOD against the MAIN camera every frame (the clone sits far from it) and
        // swaps the weapon/parts mesh back between rescans — read as the weapon rapidly resizing/jittering.
        // The cache lets EnforceLod() re-force LOD0 EVERY frame cheaply (no per-frame GetComponentsInChildren).
        _lodGroups.Clear();
        foreach (var lg in _root.GetComponentsInChildren<LODGroup>(true))
            if (lg != null) { lg.ForceLOD(0); _lodGroups.Add(lg); }
        _renderers.Clear();
        foreach (var r in _root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            // Stop sampling the world's per-object light/reflection PROBES (which override the global ambient and
            // change with day/night/menu). With probes off the renderer uses the GLOBAL ambient SH that our
            // SetLightingNow override controls → the portrait lighting is fully world-independent.
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _renderers.Add(r);
        }
    }

    // Re-force LOD0 on every cached LODGroup. Called each frame from LateUpdate: ForceLOD persists until
    // something re-evaluates the group, and the game does exactly that against the main camera every frame,
    // so a one-shot force lets the weapon flip back. Cheap — iterates the cached list, no allocation/scan.
    private void EnforceLod()
    {
        for (var i = 0; i < _lodGroups.Count; i++)
        {
            var lg = _lodGroups[i];
            if (lg != null) lg.ForceLOD(0);
        }
    }

    /// <summary>Orbit the camera around the model — dx spins (azimuth), dy tilts (elevation).</summary>
    public void Orbit(float dx, float dy)
    {
        _azimuth -= dx * 0.5f;
        _elevation = Mathf.Clamp(_elevation + dy * 0.3f, -25f, 50f);
    }

    /// <summary>Zoom — positive delta moves closer.</summary>
    public void Zoom(float delta) => _zoom = Mathf.Clamp(_zoom + delta * 0.1f, 0.4f, 3f);

    /// <summary>Pan — slide the look-at point in the camera's right/up plane (drag deltas in px).</summary>
    public void Pan(float dx, float dy)
    {
        var rot = Quaternion.Euler(_elevation, _azimuth, 0f);
        var perPixel = _rt != null && _rt.height > 0 ? 2f * (_orthoHalf / _zoom) / _rt.height : 0.004f;
        _panOffset += rot * new Vector3(-dx * perPixel, -dy * perPixel, 0f);
    }

    private void LateUpdate()
    {
        if (_rt == null || _cmd == null) return;
        // Re-scan the hierarchy periodically so clothing/armor meshes that stream in after adopt get picked up.
        if (++_rescan % 12 == 0) Rescan();
        EnforceLod();   // re-pin LOD0 every frame (the game re-evaluates the clone's LOD against the main camera)
        if (_renderers.Count == 0) return;
        var rot = Quaternion.Euler(_elevation, _azimuth, 0f);
        var lookAt = _target + _panOffset;
        var camPos = lookAt + rot * new Vector3(0f, 0f, -ViewDist);
        var view = Matrix4x4.TRS(camPos, Quaternion.LookRotation(lookAt - camPos, Vector3.up),
                                 new Vector3(1f, 1f, -1f)).inverse;
        var aspect = (float)_rt.width / _rt.height;
        // ORTHOGRAPHIC projection — no perspective foreshortening, so orbit/tilt never distorts the model's
        // proportions (the big-head/small-legs problem). Zoom scales the ortho half-height. renderIntoTexture:false
        // — on this platform (Vulkan) the true variant double-flips to upside-down.
        var halfH = _orthoHalf / _zoom;
        var halfW = halfH * aspect;
        var proj = GL.GetGPUProjectionMatrix(
            Matrix4x4.Ortho(-halfW, halfW, -halfH, halfH, 0.01f, ViewDist * 2f + 20f), renderIntoTexture: false);

        _cmd.Clear();
        _cmd.SetRenderTarget(_rt);
        // Transparent clear — the opaque pane backdrop is a separate solid Image behind the RawImage (see
        // BuildRenderHost), so the RT only carries the model + alpha; the model's own pixels are fully opaque.
        _cmd.ClearRenderTarget(true, true, new Color(0f, 0f, 0f, 0f));
        _cmd.SetViewProjectionMatrices(view, proj);
        // The view matrix has a (1,1,-1) scale → negative determinant → reversed triangle winding. Unity
        // auto-compensates culling during normal camera rendering, but for manual CommandBuffer draws we must
        // invert culling ourselves — otherwise FRONT faces get culled and we see the model's inside-out back.
        _cmd.SetInvertCulling(true);
        DrawModel(_cmd);
        _cmd.SetInvertCulling(false);
        // NOTE: the model is lit by the current SCENE — this game's custom SRP feeds the creature shaders their
        // lighting via a per-camera constant buffer that loose-global overrides can't touch (verified by a
        // red-light test). The portrait therefore reflects the scene's mood (day/night/menu). See HANDOFF §5.
        Graphics.ExecuteCommandBuffer(_cmd);
        // Regenerate mips after drawing so the RawImage's downscale samples a properly box-averaged level
        // (the RT is created mipmapped + Trilinear; autoGenerateMips is off because it doesn't fire for a
        // CommandBuffer draw, so we drive it explicitly each frame). This is what makes the 3×-supersampled
        // portrait read smooth rather than rough on shrink (user-flagged 2026-06-13).
        if (_rt.useMipMap) _rt.GenerateMips();
        if (++_frame == 120) _log?.Invoke($"[Portrait] drew {_drawItems.Count} submeshes into RT (scene-lit)");
    }

    // Collect (renderer, material, submesh) draw items, sort by render queue, and draw in two phases.
    private void DrawModel(CommandBuffer cmd)
    {
        _drawItems.Clear();
        foreach (var r in _renderers)
        {
            // Skip inactive renderers — LODGroup deactivates the non-current LOD GameObjects; drawing them too
            // would double-draw / flicker the weapon. (ForceLOD(0) in Rescan keeps LOD0 active.)
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            var mats = r.sharedMaterials;
            var n = mats != null ? mats.Length : 0;
            for (var i = 0; i < n; i++)
                if (mats![i] != null) _drawItems.Add((r, mats[i], i));
        }
        _drawItems.Sort(ByQueue);
        // Phase 1 — depth prepass for OPAQUE parts only (body, clothes, face skin; renderQueue < 2500), so the
        // model is solid/self-occluding. Transparent parts (eye cornea, hair alpha; ≥ 2500) skip it so the cornea
        // doesn't depth-occlude the iris (blank eyes).
        foreach (var (r, m, sub) in _drawItems)
            if (m.renderQueue < 2500) DrawPasses(r, m, sub, IsDepth);
        // Phase 2 — forward colour, queue-sorted (opaque first, then transparent blended on top).
        foreach (var (r, m, sub) in _drawItems)
            DrawPasses(r, m, sub, IsForwardColor);
    }
}
