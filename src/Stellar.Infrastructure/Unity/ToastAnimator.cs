using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Per-card uGUI tween driver for the animated toast stack: smooths each card's
/// <see cref="CanvasGroup"/> alpha + <see cref="RectTransform"/> localScale (enter pop /
/// exit shrink), anchored-Y reflow (exponential smoothing), and the linear countdown
/// <see cref="Image.fillAmount"/>. Stepped from the framework tick via <see cref="Step"/>
/// — <b>no Unity <c>Update()</c> by design</b> (same managed-crossing-tax avoidance as
/// <see cref="HudBarAnimator"/>): the toast canvas would otherwise cross into managed code
/// every frame. Transforms snap to target at rest so the canvas can go clean.
/// </summary>
public sealed class ToastAnimator : MonoBehaviour
{
    // Required by Il2CppInterop for managed MonoBehaviour subclasses.
    public ToastAnimator(IntPtr ptr) : base(ptr) { }

    // Easing / timing constants (seconds + closed-form coefficients), per the design spec.
    internal const float EnterDur = 0.32f;
    internal const float ExitDur = 0.26f;
    internal const float EnterScaleFrom = 0.82f;
    internal const float ExitScaleTo = 0.88f;
    internal const float ReflowSpeed = 12f;     // exp-smoothing rate for stack-Y reflow
    private const float PosSnapEpsilon = 0.25f; // px: stop writing Y within this of target

    private const float BackC1 = 1.70158f;
    private const float BackC3 = BackC1 + 1f;

    /// <summary>Per-card animation phase. ENTER and EXIT advance a 0→1 clock; LIVE rests at scale 1.</summary>
    internal enum Phase { Enter, Live, Exit }

    /// <summary>Mutable per-card tween state, owned by the renderer (which adds/removes cards) and
    /// advanced here. A class (not struct) so the renderer holds a live reference per card id.</summary>
    internal sealed class Card
    {
        public CanvasGroup Group = null!;
        public RectTransform Rect = null!;
        public Image? Countdown;        // bottom fill bar; null if the card has none
        public Phase State = Phase.Enter;
        public float Clock;             // seconds into the current ENTER/EXIT phase
        public float TargetY;           // stack-Y the card should reflow toward (negative = downward)
        public bool PosInit;            // false until the first reflow seeds the current Y
        public Func<float> Countdown01 = () => 1f;  // clamp01((ExpiresAt-now)/Duration)
        public bool Finished;           // set when EXIT completes → renderer destroys the GO
    }

    /// <summary>Advance one card one tick. Returns when the card is mid-exit and finishes its EXIT
    /// (sets <see cref="Card.Finished"/>) — the renderer then destroys it. Pure transform writes;
    /// no allocation. <paramref name="dt"/> is seconds since the previous framework tick.</summary>
    internal void StepCard(Card c, float dt)
    {
        if (c.Rect == null || c.Group == null) return;

        // Reflow Y toward target (exp-smoothing; snaps at rest so the canvas goes clean).
        var pos = c.Rect.anchoredPosition;
        if (!c.PosInit) { pos.y = c.TargetY; c.PosInit = true; }
        else if (Mathf.Abs(c.TargetY - pos.y) > PosSnapEpsilon)
            pos.y = Mathf.Lerp(pos.y, c.TargetY, Mathf.Clamp01(dt * ReflowSpeed));
        else pos.y = c.TargetY;
        c.Rect.anchoredPosition = pos;

        switch (c.State)
        {
            case Phase.Enter: StepEnter(c, dt); break;
            case Phase.Exit:  StepExit(c, dt); break;
            default:          StepLive(c); break;
        }

        if (c.Countdown != null && c.State != Phase.Exit)   // freeze countdown during exit
        {
            float f = Mathf.Clamp01(c.Countdown01());
            if (Mathf.Abs(f - c.Countdown.fillAmount) > 0.0005f) c.Countdown.fillAmount = f;
        }
    }

    private static void StepEnter(Card c, float dt)
    {
        c.Clock += dt;
        float t = Mathf.Clamp01(c.Clock / EnterDur);
        float scale = Mathf.Lerp(EnterScaleFrom, 1f, EaseOutBack(t));
        ApplyScale(c.Rect, scale);
        c.Group.alpha = EaseOutQuad(t);
        if (t >= 1f) { ApplyScale(c.Rect, 1f); c.Group.alpha = 1f; c.State = Phase.Live; c.Clock = 0f; }
    }

    private static void StepExit(Card c, float dt)
    {
        c.Clock += dt;
        float t = Mathf.Clamp01(c.Clock / ExitDur);
        float e = EaseInQuad(t);
        ApplyScale(c.Rect, Mathf.Lerp(1f, ExitScaleTo, e));
        c.Group.alpha = 1f - e;
        if (t >= 1f) { c.Group.alpha = 0f; c.Finished = true; }
    }

    private static void StepLive(Card c)
    {
        // Resting state: ensure clean targets (one snap, then no per-tick writes that dirty the canvas).
        if (c.Group.alpha != 1f) c.Group.alpha = 1f;
        if (c.Rect.localScale.x != 1f) ApplyScale(c.Rect, 1f);
    }

    /// <summary>Begin the EXIT phase if the card isn't already exiting. Idempotent.</summary>
    internal static void BeginExit(Card c)
    {
        if (c.State == Phase.Exit) return;
        c.State = Phase.Exit;
        c.Clock = 0f;
    }

    private static void ApplyScale(RectTransform rt, float s) => rt.localScale = new Vector3(s, s, 1f);

    // easeOutBack: 1 + c3*(t-1)^3 + c1*(t-1)^2  (overshoots then settles).
    private static float EaseOutBack(float t)
    {
        float p = t - 1f;
        return 1f + BackC3 * p * p * p + BackC1 * p * p;
    }

    private static float EaseOutQuad(float t) => 1f - (1f - t) * (1f - t);

    private static float EaseInQuad(float t) => t * t;
}
