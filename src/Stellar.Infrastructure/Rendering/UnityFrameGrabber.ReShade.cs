using System;
using System.Collections;
using System.Collections.Generic;
using Stellar.Application.Abstractions;
using Stellar.Application.Imaging;
using Stellar.Application.Services;
using Stellar.Abstractions.Domain;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// ReShade in the capture (design § 11): a <see cref="ReShadeCapturePlanner"/> is advanced ONE step per frame, each at
/// the end of the frame — never two <c>render_effects</c> in one frame. WarmUp renders into a scratch target of the
/// photo's size until ReShade reports techniques drawn (the first render at a new size draws nothing); Render is the real
/// capture with the bridge's render event between the camera render and the readback, then alpha forced to 255 (ReShade
/// leaves it 0). A shaped photo switches its depth-using techniques off first, as temporary (never saved) overrides that
/// are restored after the render — and in every exit path (failure, timeout, host loss). On warm-up timeout the photo is
/// taken without ReShade and carries <see cref="ReShadeCaptureOptions.NotReadyNote"/>.
/// </summary>
internal sealed partial class UnityFrameGrabber
{
    private const int ReShadeRenderEventId = 1;   // the add-on ignores the id; the probe used 1

    private readonly ReShadeBridge? _reShade;
    private RenderTexture? _warmUpTarget;              // scratch target, reused across one capture's warm-up frames
    private ReShadeTechniqueRef[]? _depthOverrides;    // switched off for the capture in flight; null = none held
    private int _renderCode = NoRealRender;            // LastRender after the real render (diagnostics)
    private const int NoRealRender = int.MinValue;

    private sealed class GrabBox
    {
        internal FrameGrab? Value;
    }

    private IEnumerator CaptureFrames(GrabTarget target, CaptureFormat format, int q, GrabBox box)
    {
        if (target.ReShade is not { } options || _reShade is null || _reShade.RenderEventFunc() == IntPtr.Zero)
        {
            yield return new WaitForEndOfFrame();
            var plain = Capture(target, format, q);
            box.Value = target.ReShade is null ? plain : plain with { Note = ReShadeCaptureOptions.NotReadyNote };
            yield break;
        }
        var planner = new ReShadeCapturePlanner(options.Shaped, options.Active, Environment.TickCount64);
        var started = Environment.TickCount64;
        _renderCode = NoRealRender;
        var queued = false;   // LastRender reflects THIS capture only after its first warm-up render
        try
        {
            while (box.Value is null)
            {
                yield return new WaitForEndOfFrame();
                var step = planner.Next(queued ? _reShade.LastRender() : 0, Environment.TickCount64);
                if (step is CaptureStep.WarmUp) queued = true;
                box.Value = RunStep(step, target, format, q);
            }
        }
        finally
        {
            RestoreDepthOverrides();
            ReleaseWarmUpTarget();
            OnReShadeCapture(planner, _renderCode, box.Value is { Note: null }, Environment.TickCount64 - started);
        }
    }

    // One planner step; returns the grab once the photo is taken (Render, or Done without ReShade), else null.
    private FrameGrab? RunStep(CaptureStep step, GrabTarget target, CaptureFormat format, int q)
    {
        switch (step)
        {
            case CaptureStep.DisableDepth disable:
                DisableDepthOverrides(disable.Techniques);
                return null;
            case CaptureStep.WarmUp:
                WarmUpRender(target.Size.Width, target.Size.Height);
                return null;
            case CaptureStep.Restore:
                RestoreDepthOverrides();
                return null;
            case CaptureStep.Render:
                return RenderWithReShade(target, format, q);
            default:   // Done(false): timed out (or nothing to draw) — the photo goes ahead without ReShade
                return Capture(target, format, q) with { Note = ReShadeCaptureOptions.NotReadyNote };
        }
    }

    private FrameGrab RenderWithReShade(GrabTarget target, CaptureFormat format, int q)
    {
        FrameGrab grab;
        try
        {
            grab = Capture(target, format, q, applyReShade: true);
        }
        finally
        {
            RestoreDepthOverrides();   // right after the real render: its effects are already drawn and read back
        }
        // The readback synced with the render thread, so LastRender is this render's result.
        _renderCode = _reShade!.LastRender();
        if (_renderCode <= 0) return grab with { Note = ReShadeCaptureOptions.NotReadyNote };
        RgbaAlpha.ForceOpaque(grab.RgbaBottomUp);   // empty for JPG (encoded already; JPG carries no alpha)
        return grab;
    }

    private void WarmUpRender(int w, int h)
    {
        var cam = Camera.main;
        if (cam == null) throw new FrameGrabException("No camera is rendering the scene.");
        var rt = WarmUpTarget(w, h);
        var prevTarget = cam.targetTexture;
        try
        {
            cam.targetTexture = rt;
            // The real camera view, as specified. Only the target SIZE decides ReShade's permutation, so this render may
            // be droppable — its cost (heavy at 4x, every warm-up frame) is to be measured in game before changing it.
            cam.Render();
        }
        finally
        {
            cam.targetTexture = prevTarget;
        }
        IssueReShadeRender(rt, w, h);
    }

    private RenderTexture WarmUpTarget(int w, int h)
    {
        if (_warmUpTarget != null && _warmUpTarget.width == w && _warmUpTarget.height == h) return _warmUpTarget;
        ReleaseWarmUpTarget();
        var rt = new RenderTexture(w, h, 24);   // the capture's own target shape, so the warmed permutation is the one used
        if (!rt.Create())
        {
            UnityEngine.Object.Destroy(rt);
            throw new FrameGrabException($"A {w}x{h} render target could not be created.");
        }
        return _warmUpTarget = rt;
    }

    // Released after every capture: a 4x scratch target is ~260 MB of VRAM. ReShade keeps the compiled permutation per
    // size, so the next photo at this size warms up in one frame regardless.
    private void ReleaseWarmUpTarget()
    {
        if (_warmUpTarget == null) return;
        _warmUpTarget.Release();
        UnityEngine.Object.Destroy(_warmUpTarget);
        _warmUpTarget = null;
    }

    private void IssueReShadeRender(RenderTexture rt, int w, int h)
    {
        var bridge = _reShade!;
        bridge.QueueRender(rt.GetNativeTexturePtr(), (uint)w, (uint)h);
        var callback = bridge.RenderEventFunc();
        if (callback != IntPtr.Zero) GL.IssuePluginEvent(callback, ReShadeRenderEventId);
    }

    private void DisableDepthOverrides(IReadOnlyList<ReShadeTechniqueRef> techniques)
    {
        RestoreDepthOverrides();
        var held = new ReShadeTechniqueRef[techniques.Count];
        for (var i = 0; i < held.Length; i++) held[i] = techniques[i];
        _depthOverrides = held;   // recorded first: a restore must cover every request that may have gone out
        foreach (var t in held) _reShade?.RequestTechnique(t.EffectFile, t.Name, on: false, save: false);
    }

    private void RestoreDepthOverrides()
    {
        if (_depthOverrides is not { } held) return;
        _depthOverrides = null;
        foreach (var t in held) _reShade?.RequestTechnique(t.EffectFile, t.Name, on: true, save: false);
    }
}
