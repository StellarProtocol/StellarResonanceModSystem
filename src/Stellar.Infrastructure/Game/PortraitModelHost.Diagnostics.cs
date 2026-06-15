// src/Stellar.Infrastructure/Game/PortraitModelHost.Diagnostics.cs
using System;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Diagnostics;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic-mode logging for <see cref="PortraitModelHost"/> — the hard-won probes that localised the portrait
/// render pipeline (renderer state, RT pixel content, and the game's render-pass dictionary). All entry points
/// short-circuit on <see cref="StellarDiagnostics.IsEnabled"/> so the production partial calls them
/// unconditionally — keeps the render path clean of inline gates (per coding-standards § Diagnostics). These do
/// not affect rendering; they explain WHY a portrait is/ isn't drawing. Renderer-side draw diagnostics live in
/// <see cref="Stellar.Infrastructure.Unity.PortraitCmdRenderer"/>.
/// </summary>
internal sealed partial class PortraitModelHost
{
    private int _diagPolls;

    // Called once after a model is prepared: dump the collected renderers + the game's render-pass state.
    private void DiagAfterPrepare(object? renders)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _diagPolls = 0;
        if (renders != null) DiagDumpRenderers(renders);
        DiagRenderPassState();
    }

    // Per-frame from the Texture getter: sample the RT every ~3s to confirm the model is STILL drawing. Made
    // periodic (was a one-shot at poll 180) so a held-open repro catches the intermittent-black-box defect —
    // a later poll reading nonZeroSamples=0 after an earlier >0 pinpoints when the draw stops landing.
    private void DiagTexturePoll()
    {
        if (!StellarDiagnostics.IsEnabled || _rt == null) return;
        if (++_diagPolls % 180 != 0) return;
        DiagPixelProbe(_rt);
    }

    // The render state of the collected renderers: active GameObject, enabled, layer, shader, bounds. Tells you
    // whether the model is even drawable (inactive/disabled = created-but-not-shown) and which shaders it uses.
    private void DiagDumpRenderers(object renders)
    {
        var count = PortraitReflect.Get(renders, "Count") is int c ? c : 0;
        for (var i = 0; i < count && i < 3; i++)
        {
            object? item;
            try { item = PortraitReflect.Invoke(renders, "get_Item", i) ?? PortraitReflect.Invoke(renders, "Get", i); }
            catch (Exception ex) { _log.Info($"[Portrait] renderer[{i}] get failed: {PortraitReflect.Unwrap(ex)}"); continue; }
            var r = item as Renderer ?? (item as Il2CppObjectBase)?.TryCast<Renderer>();
            if (r is null) { _log.Info($"[Portrait] renderer[{i}] not a Renderer ({item?.GetType().Name})"); continue; }
            var shader = r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "<none>";
            _log.Info($"[Portrait] renderer[{i}] '{r.gameObject.name}' active={r.gameObject.activeInHierarchy} " +
                      $"enabled={r.enabled} layer={r.gameObject.layer} shader='{shader}' boundsSize={r.bounds.size}");
        }
    }

    // Sample the RT for non-zero pixels — decisive "is the model rendering into our texture" check.
    private void DiagPixelProbe(RenderTexture rt)
    {
        try
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var snap = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            snap.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            snap.Apply(false);
            RenderTexture.active = prev;
            var nonZero = 0;
            for (var y = 0; y < rt.height; y += Math.Max(1, rt.height / 20))
                for (var x = 0; x < rt.width; x += Math.Max(1, rt.width / 20))
                    if (snap.GetPixel(x, y).maxColorComponent > 0.01f) nonZero++;
            UnityEngine.Object.Destroy(snap);
            _log.Info($"[Portrait] pixel probe: nonZeroSamples={nonZero}");
        }
        catch (Exception e) { _log.Info($"[Portrait] pixel probe failed: {e.GetType().Name}: {e.Message}"); }
    }

    // Read the AOT render pass's own dictionary (ZModelSnapshotRenderPass.ModelInfoList: RenderTexture →
    // ZModel2RTInfo) — the dict the game's pass draws from. We no longer register with it (ZModel2RT.Add leaked),
    // so this normally reports count=0; kept because it was the key probe that mapped the AOT/hot-update boundary.
    private void DiagRenderPassState()
    {
        try
        {
            var passType = _types.FindType("Bokura.Rendering.ZModelSnapshotRenderPass");
            var inst = passType is null ? null : PortraitReflect.GetStatic(passType, "Instance");
            var dict = inst is null ? null : PortraitReflect.Get(inst, "ModelInfoList");
            var count = dict is null ? "<no dict>" : PortraitReflect.Get(dict, "Count")?.ToString();
            _log.Info($"[Portrait] render-pass ModelInfoList count={count}");
            if (dict is null || _rt is null) return;
            var info = FirstValue(dict);
            if (info is null) return;
            var rends = PortraitReflect.Get(info, "Renders");
            var infoRt = PortraitReflect.Get(info, "RT") as RenderTexture;
            _log.Info($"[Portrait] info: Frame={PortraitReflect.Get(info, "Frame")} IsPlaying={PortraitReflect.Get(info, "IsPlaying")} " +
                      $"Renders={(rends is null ? "?" : PortraitReflect.Get(rends, "Count"))} " +
                      $"RtUber={(PortraitReflect.Get(info, "RtUberMaterial") != null)} " +
                      $"infoRt={(infoRt != null ? $"{infoRt.width}x{infoRt.height}" : "null")}");
        }
        catch (Exception e) { _log.Info($"[Portrait] render-pass probe failed: {PortraitReflect.Unwrap(e)}"); }
    }

    // First value in a ZDictionary via its Values collection enumerator (reflection over the boxed struct).
    private static object? FirstValue(object dict)
    {
        var values = PortraitReflect.Get(dict, "Values");
        if (values is null) return null;
        var en = PortraitReflect.Invoke(values, "GetEnumerator");
        if (en is null) return null;
        if (PortraitReflect.Invoke(en, "MoveNext") is true) return PortraitReflect.Get(en, "Current");
        return null;
    }
}
