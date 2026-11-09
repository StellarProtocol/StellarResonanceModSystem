using System;
using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

// The Unity-free half of ConditionalElement.KeepAlive (the GameObject side is WindowBuilder.KeepAlive.cs): the branch
// states, what each event does to them, the binding gate and the re-warm plan. Pure, so the rules are unit-tested.

/// <summary>Where a KeepAlive branch is. <b>Cold</b>: its container is inactive (never shown, or demoted) — showing it
/// costs a full activation, like a plain Conditional. <b>Warming</b>: active, parked off-screen with its canvas off, its
/// units being re-activated a few per frame (layout + meshes built in the background). <b>Parked</b>: active, off-screen,
/// canvas off, bindings idle — showing it only turns its canvas back on. <b>Shown</b>: the visible branch.</summary>
internal enum KeepAliveState { Cold, Warming, Parked, Shown }

/// <summary>The GameObject work a transition needs (WindowBuilder.KeepAliveHost carries it out).</summary>
internal enum KeepAliveAction
{
    None,
    Activate,     // Cold → Shown: SetActive(true) in its slot (the full first-show cost)
    FinishWarm,   // Warming → Shown: re-activate the rest of its units now, then show
    Unpark,       // Parked → Shown: back into the layout, canvas + raycaster on, bindings on
    Park,         // Shown → Parked: out of the layout, off-screen, canvas + raycaster off, bindings off
    Deactivate,   // → Cold: SetActive(false) (a hidden branch that was never kept alive, or one without units)
    Rewarm,       // window re-shown: units back off while the window root is still inactive, warm them again
}

internal static class KeepAliveRules
{
    /// <summary>The branch becomes the visible one.</summary>
    public static (KeepAliveState Next, KeepAliveAction Act) OnShow(KeepAliveState s) => s switch
    {
        KeepAliveState.Cold    => (KeepAliveState.Shown, KeepAliveAction.Activate),
        KeepAliveState.Warming => (KeepAliveState.Shown, KeepAliveAction.FinishWarm),
        KeepAliveState.Parked  => (KeepAliveState.Shown, KeepAliveAction.Unpark),
        _                      => (KeepAliveState.Shown, KeepAliveAction.None),
    };

    /// <summary>Another branch became the visible one. A Cold branch is (re)deactivated exactly like a plain Conditional
    /// (the first apply hides every non-active tab this way); a warming one keeps warming.</summary>
    public static (KeepAliveState Next, KeepAliveAction Act) OnHide(KeepAliveState s) => s switch
    {
        KeepAliveState.Shown => (KeepAliveState.Parked, KeepAliveAction.Park),
        KeepAliveState.Cold  => (KeepAliveState.Cold, KeepAliveAction.Deactivate),
        _                    => (s, KeepAliveAction.None),
    };

    /// <summary>The WINDOW is about to be re-shown (its root re-activated). Activating the root would re-enable every
    /// kept-alive branch at once — OnEnable re-dirties every graphic, so all tabs would rebuild in that one frame. A branch
    /// with recorded units is re-warmed in the background instead; one without units (built lazily, no Prebuild) goes
    /// cold. The shown branch re-activates with the window, as any visible content does.</summary>
    public static (KeepAliveState Next, KeepAliveAction Act) OnWindowReshow(KeepAliveState s, bool hasUnits) => s switch
    {
        KeepAliveState.Parked or KeepAliveState.Warming when hasUnits => (KeepAliveState.Warming, KeepAliveAction.Rewarm),
        KeepAliveState.Parked or KeepAliveState.Warming               => (KeepAliveState.Cold, KeepAliveAction.Deactivate),
        _                                                             => (s, KeepAliveAction.None),
    };

    /// <summary>The units to re-activate when re-warming, in build order: every unit still waiting from an interrupted warm
    /// (inactive, ours) plus every unit active now (the caller deactivates those). A unit inactive for another reason (a
    /// binding hid it) is left alone, so re-warming never shows what a binding hid.</summary>
    public static List<T> RewarmPlan<T>(IReadOnlyList<T> units, ISet<T> pending, Func<T, bool> isActive) where T : class
    {
        var plan = new List<T>();
        for (var i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (u != null && (pending.Contains(u) || isActive(u))) plan.Add(u);
        }
        return plan;
    }
}

/// <summary>One per KeepAlive branch: whether its bindings run this apply. Every binding built inside the branch is tagged
/// with it (WindowBuilder.TagAlive); WindowToken.Apply skips a tagged binding while its gate is not live — a parked tab
/// costs no binding work. Live while shown or warming (warming fills its texts before it parks), and only while every
/// enclosing KeepAlive branch is live too.</summary>
internal sealed class AliveGate
{
    public AliveGate? Parent;
    public bool Shown;
    public bool Warming;
    public bool Live => (Shown || Warming) && (Parent == null || Parent.Live);
}

/// <summary>Base of every per-apply binding: the KeepAlive gate it was built under (null = not in a KeepAlive branch,
/// always applied).</summary>
internal abstract class GatedBinding
{
    public AliveGate? Alive;

    /// <summary>Apply this binding now? (null-safe for the interface-typed variable-list bindings).</summary>
    public static bool Runs(GatedBinding? b) => b?.Alive == null || b.Alive.Live;
}
