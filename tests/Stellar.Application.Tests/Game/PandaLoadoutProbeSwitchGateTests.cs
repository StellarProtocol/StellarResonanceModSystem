using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED — the two pre-dispatch gates on a loadout switch.
///
/// <para><b>Origin: owner report 2026-09-05.</b> Switching the LoadoutSwitcher to the SAME loadout 2-3
/// times in a row froze the client for <b>3,261 ms</b> (frametime max) and then stuttered; the log showed
/// <c>[LoadoutSwitcher] Switched to Beam</c> eight times consecutively. The framework dispatched every
/// request, on a comment's claim that "the game itself cheaply no-ops a switch to the already-active
/// loadout". REFUTED by the game's own Lua: <c>weapon_vm.lua:509-514</c> builds
/// <c>{oldProjectId = CurPlanId, newProjectId = planId}</c> and fires <c>WorldProxy.SwitchProject</c>
/// UNCONDITIONALLY, then (<c>:536-542</c>) saves <c>currentProjectSyncData</c> and dispatches
/// <c>OnRolePlanChange</c> — a full server re-equip whose <c>CharSerialize</c> delta burst drives the
/// per-class resolve walk (see <see cref="PandaLoadoutProbeResolveGateTests"/>). The game's own dropdown
/// applies the two checks pinned here before it ever calls the wrapper:</para>
/// <list type="number">
///   <item><b>Already active</b> — <c>role_plan_loop_item.lua:66</c> ignores the click when
///   <c>CurPlanId == PlanId</c> (the Switch action is not even offered).</item>
///   <item><b>3 s cooldown</b> — <c>role_plan_loop_item.lua:118-121</c> refuses while
///   <c>now - SwitchRolePlanTime &lt; CombatStrategySswitchCd</c> (<c>Global.lua:1703</c> = 3), tip
///   150208.</item>
/// </list>
/// <para>Order is load-bearing and mirrors the game's: already-active is decided FIRST (line 66 returns
/// before the cooldown test at line 118), so re-pressing the worn loadout inside the cooldown reads as
/// "already active", never as "cooling down".</para>
///
/// <para>The dropdown's third check (in battle, <c>:122-125</c>, tip 150206) is deliberately NOT
/// duplicated here: that refusal already comes from the server + the game's own wrapper toast, which
/// <c>Evaluate</c> maps to <see cref="LoadoutResult.Rejected"/>.</para>
/// </summary>
public sealed class PandaLoadoutProbeSwitchGateTests
{
    private const int Unknown = PandaLoadoutProbe.UnknownPlanId;

    // ── DecideSwitch (pure) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Same_loadout_is_not_dispatched_2026_09_05_same_loadout_freeze()
    {
        // The 3,261 ms freeze: press "Beam" while already wearing "Beam".
        Assert.Equal(PandaLoadoutProbe.SwitchDecision.AlreadyActive,
            PandaLoadoutProbe.DecideSwitch(liveCurrentPlanId: 7, targetId: 7, lastSwitchMs: 0, nowMs: 1_000_000));
    }

    [Fact]
    public void Different_loadout_dispatches()
    {
        Assert.Equal(PandaLoadoutProbe.SwitchDecision.Dispatch,
            PandaLoadoutProbe.DecideSwitch(liveCurrentPlanId: 7, targetId: 8, lastSwitchMs: 0, nowMs: 1_000_000));
    }

    [Fact]
    public void Repress_within_the_games_three_second_cooldown_is_refused()
    {
        // Global.lua:1703 CombatStrategySswitchCd = 3 → 3,000 ms.
        Assert.Equal(PandaLoadoutProbe.SwitchDecision.CooldownActive,
            PandaLoadoutProbe.DecideSwitch(liveCurrentPlanId: 7, targetId: 8, lastSwitchMs: 1_000_000, nowMs: 1_002_999));
    }

    [Fact]
    public void Press_once_the_cooldown_has_elapsed_dispatches()
    {
        Assert.Equal(PandaLoadoutProbe.SwitchDecision.Dispatch,
            PandaLoadoutProbe.DecideSwitch(liveCurrentPlanId: 7, targetId: 8, lastSwitchMs: 1_000_000, nowMs: 1_003_000));
    }

    [Fact]
    public void Already_active_beats_the_cooldown_mirroring_the_games_own_check_order()
    {
        // role_plan_loop_item.lua:66 returns before the cooldown test at :118.
        Assert.Equal(PandaLoadoutProbe.SwitchDecision.AlreadyActive,
            PandaLoadoutProbe.DecideSwitch(liveCurrentPlanId: 7, targetId: 7, lastSwitchMs: 1_000_000, nowMs: 1_000_500));
    }

    [Fact]
    public void An_unknown_current_plan_always_dispatches()
    {
        // Before the first parse (and after logout) we do not know the worn plan. Never swallow the
        // request on a guess — that is the failure the removed fast-path comment warned about (a stale
        // match made the login-current loadout permanently un-switchable).
        Assert.Equal(PandaLoadoutProbe.SwitchDecision.Dispatch,
            PandaLoadoutProbe.DecideSwitch(Unknown, targetId: 7, lastSwitchMs: 0, nowMs: 1_000_000));
    }

    [Fact]
    public void The_synthesized_current_entry_is_never_read_as_already_active()
    {
        // LiveCurrentIndex (-1) is the synthesized "Current" entry a caller CAN pass as a target, so the
        // unknown sentinel must not be -1: an unknown current + a -1 target must still dispatch.
        Assert.Equal(PandaLoadoutProbe.SwitchDecision.Dispatch,
            PandaLoadoutProbe.DecideSwitch(Unknown, targetId: -1, lastSwitchMs: 0, nowMs: 1_000_000));
    }

    [Fact]
    public void A_never_dispatched_probe_is_not_treated_as_cooling_down()
    {
        // lastSwitchMs == 0 means "never dispatched", not "dispatched at tick 0".
        Assert.Equal(PandaLoadoutProbe.SwitchDecision.Dispatch,
            PandaLoadoutProbe.DecideSwitch(liveCurrentPlanId: 7, targetId: 8, lastSwitchMs: 0, nowMs: 500));
    }

    // ── The gate is actually WIRED into CallApplyAsync ────────────────────────────────────────────
    // A pure decision nobody consults fixes nothing. These drive the real entry point with the bridge
    // flag forced resolved; every gated branch returns before any Lua/IL2CPP call, and the dispatch
    // branch only enqueues (the Lua chunk fires later, on the main-thread drain).

    private sealed class FakeTypeRegistry : IGameTypeRegistry
    {
        public Type? FindType(string fullName) => null;
    }

    private static PandaLoadoutProbe ResolvedProbe(int liveCurrentPlanId, long lastSwitchMs)
    {
        var probe = new PandaLoadoutProbe(new StubLog(), new FakeTypeRegistry());
        SetField(probe, "_bridgeResolved", true);
        SetField(probe, "_liveCurrentPlanId", liveCurrentPlanId);
        SetField(probe, "_lastSwitchDispatchMs", lastSwitchMs);
        return probe;
    }

    private static void SetField(PandaLoadoutProbe probe, string name, object value)
    {
        var f = typeof(PandaLoadoutProbe).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(f);
        f!.SetValue(probe, value);
    }

    private static int QueuedDispatches(PandaLoadoutProbe probe) => QueueCount(probe, "_toDispatch");

    private static int QueuedTips(PandaLoadoutProbe probe) => QueueCount(probe, "_toTip");

    private static int QueueCount(PandaLoadoutProbe probe, string fieldName)
    {
        var f = typeof(PandaLoadoutProbe).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(f);
        return ((ICollection)f!.GetValue(probe)!).Count;
    }

    [Fact]
    public void CallApplyAsync_on_the_worn_loadout_completes_Success_without_queueing_a_dispatch()
    {
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: 0);

        var task = ((ILoadoutProbe)probe).CallApplyAsync(7, CancellationToken.None);

        Assert.True(task.IsCompleted);
        Assert.Equal(LoadoutResult.Success, task.Result);
        Assert.Equal(0, QueuedDispatches(probe));   // nothing sent to the server
    }

    [Fact]
    public void CallApplyAsync_inside_the_cooldown_completes_Rejected_without_queueing_a_dispatch()
    {
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: Environment.TickCount64);

        var task = ((ILoadoutProbe)probe).CallApplyAsync(8, CancellationToken.None);

        Assert.True(task.IsCompleted);
        Assert.Equal(LoadoutResult.Rejected, task.Result);
        Assert.Equal(0, QueuedDispatches(probe));
    }

    [Fact]
    public void CallApplyAsync_for_a_different_loadout_outside_the_cooldown_queues_exactly_one_dispatch()
    {
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: Environment.TickCount64 - 4_000);

        var task = ((ILoadoutProbe)probe).CallApplyAsync(8, CancellationToken.None);

        Assert.False(task.IsCompleted);   // waits for the game's wrapper result
        Assert.Equal(1, QueuedDispatches(probe));
    }

    [Fact]
    public void A_skipped_switch_does_not_arm_the_cooldown_for_a_real_one()
    {
        // Pressing the worn loadout sends nothing, so it must not start the 3 s clock — the very next
        // press of a DIFFERENT loadout has to go through.
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: 0);

        var skipped = ((ILoadoutProbe)probe).CallApplyAsync(7, CancellationToken.None);
        Assert.True(skipped.IsCompleted);   // never block on .Result — a regression here would hang, not fail
        Assert.Equal(LoadoutResult.Success, skipped.Result);

        var task = ((ILoadoutProbe)probe).CallApplyAsync(8, CancellationToken.None);

        Assert.False(task.IsCompleted);
        Assert.Equal(1, QueuedDispatches(probe));
    }

    [Fact]
    public void A_dispatched_switch_arms_the_cooldown_so_the_immediate_re_press_is_refused()
    {
        // The eight-consecutive-switches shape, with distinct targets: only the first reaches the game.
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: 0);

        var first = ((ILoadoutProbe)probe).CallApplyAsync(8, CancellationToken.None);
        var second = ((ILoadoutProbe)probe).CallApplyAsync(9, CancellationToken.None);

        Assert.False(first.IsCompleted);
        Assert.True(second.IsCompleted);
        Assert.Equal(LoadoutResult.Rejected, second.Result);
        Assert.Equal(1, QueuedDispatches(probe));
    }

    // ── The refusal is VISIBLE: the game's own tip ────────────────────────────────────────────────
    // The LoadoutSwitcher plugin's Report() toasts ONLY on Success (Plugin.cs:141), so a Rejected result
    // is log-only. The dropdown's own answer to this exact case is tip 150208
    // (role_plan_loop_item.lua:119) — so the framework queues that same tip for the main-thread drain.

    [Fact]
    public void The_tip_chunk_is_the_games_own_call_shape()
    {
        // role_plan_loop_item.lua:119 is ((Z.TipsVM).ShowTips)(150208) — a DOT call on Z.TipsVM, not
        // ShowTipsLang and not a colon call. pcall-guarded because we invoke it bare, where the game calls
        // it from inside create_coro_xpcall.
        Assert.Equal("pcall(function() Z.TipsVM.ShowTips(150208) end)",
            PandaLoadoutProbe.BuildShowTipsChunk(PandaLoadoutProbe.SwitchCooldownTipId));
    }

    [Fact]
    public void The_cooldown_refusal_queues_exactly_one_of_the_games_own_tips()
    {
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: Environment.TickCount64);

        var task = ((ILoadoutProbe)probe).CallApplyAsync(8, CancellationToken.None);

        Assert.True(task.IsCompleted);
        Assert.Equal(LoadoutResult.Rejected, task.Result);
        Assert.Equal(1, QueuedTips(probe));   // one per refused press — nothing on this path fires per tick
    }

    [Fact]
    public void An_already_active_skip_queues_no_tip()
    {
        // The dropdown shows nothing here either — it simply does not offer Switch (lua:66). The plugin's
        // green "Switched to …" toast is the feedback.
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: 0);

        ((ILoadoutProbe)probe).CallApplyAsync(7, CancellationToken.None);

        Assert.Equal(0, QueuedTips(probe));
    }

    [Fact]
    public void A_dispatched_switch_queues_no_tip()
    {
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: 0);

        ((ILoadoutProbe)probe).CallApplyAsync(8, CancellationToken.None);

        Assert.Equal(0, QueuedTips(probe));
    }

    [Fact]
    public void Mashing_the_hotkey_never_grows_the_tip_queue_without_bound()
    {
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: Environment.TickCount64);

        for (var i = 0; i < 50; i++) ((ILoadoutProbe)probe).CallApplyAsync(8, CancellationToken.None);

        Assert.InRange(QueuedTips(probe), 1, 3);   // MaxQueuedTips — a stalled drain can never back up
    }

    // ── Logout resets the gate state ──────────────────────────────────────────────────────────────

    [Fact]
    public void ClearSession_forgets_the_worn_plan_and_the_cooldown()
    {
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: Environment.TickCount64);

        probe.ClearSession();

        // The next character's worn plan is unknown, and its switch cooldown is its own.
        var task = ((ILoadoutProbe)probe).CallApplyAsync(7, CancellationToken.None);
        Assert.False(task.IsCompleted);
        Assert.Equal(1, QueuedDispatches(probe));
    }

    [Fact]
    public void ClearSession_drops_a_tip_owed_to_the_previous_character()
    {
        var probe = ResolvedProbe(liveCurrentPlanId: 7, lastSwitchMs: Environment.TickCount64);
        ((ILoadoutProbe)probe).CallApplyAsync(8, CancellationToken.None);
        Assert.Equal(1, QueuedTips(probe));   // sanity: the refusal really queued one

        probe.ClearSession();

        Assert.Equal(0, QueuedTips(probe));
    }
}
