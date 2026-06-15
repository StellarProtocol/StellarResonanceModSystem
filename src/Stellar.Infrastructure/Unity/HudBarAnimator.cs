using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Per-bar smoothing for the uGUI HUD toolkit: lerps every registered bar's <c>Image.fillAmount</c>
/// toward the latest target so bars animate smoothly even though HudService re-pulls values only at
/// the ~10 Hz cap. One instance lives on the HUD canvas; <c>HudRenderer</c> registers each bar's
/// (fill image, target getter) as it builds.
///
/// <para><b>No Unity <c>Update()</c> by design.</b> The HUD canvas is always-on in-world, so an
/// injected-MonoBehaviour per-frame <c>Update</c> here would cross into the managed runtime every
/// frame — the ~12-18 fps managed-crossing tax. Instead <see cref="Step"/> is driven from
/// <c>HudService.Tick</c> on the throttled framework ticker (no per-frame entry).</para>
/// </summary>
public sealed class HudBarAnimator : MonoBehaviour
{
    // Required by Il2CppInterop for managed MonoBehaviour subclasses.
    public HudBarAnimator(IntPtr ptr) : base(ptr) { }

    internal readonly System.Collections.Generic.List<(Image Img, Func<float> Target)> Bars = new();
    private const float Speed = 12f;   // lerp rate; tuned in-game
    // Skip the write (snap once) when within this of target: Mathf.Lerp is asymptotic, so writing
    // fillAmount forever keeps the Image dirty → a canvas rebuild every tick even when HP/MP is
    // static. Stopping at rest lets the canvas go clean. 0.0005 = sub-pixel on any real bar.
    private const float SnapEpsilon = 0.0005f;
    private int _throwLogged;

    /// <summary>Forget bars whose image was destroyed (HUD removed / scene change) so the list
    /// doesn't grow unbounded across mount/unmount cycles.</summary>
    internal void Prune()
    {
        for (var i = Bars.Count - 1; i >= 0; i--)
            if (Bars[i].Img == null) Bars.RemoveAt(i);
    }

    /// <summary>Advance every bar one tick toward its target. Driven by HudService.Tick (on the
    /// throttled framework ticker) — NOT a Unity Update message. <paramref name="dt"/> is seconds
    /// since the previous tick, so convergence speed is correct at any tick rate.</summary>
    internal void Step(float dt)
    {
        var k = dt * Speed;
        for (var i = 0; i < Bars.Count; i++)
        {
            var (img, target) = Bars[i];
            if (img == null) continue;
            float t;
            try { t = Mathf.Clamp01(target()); }
            catch { if (_throwLogged++ == 0) Debug.LogWarning("[Hud] bar target threw (rate-limited)"); continue; }
            var cur = img.fillAmount;
            if (Mathf.Abs(t - cur) <= SnapEpsilon)
            {
                if (cur != t) img.fillAmount = t;   // one final snap, then leave the canvas clean
                continue;
            }
            img.fillAmount = Mathf.Lerp(cur, t, k);
        }
    }
}
