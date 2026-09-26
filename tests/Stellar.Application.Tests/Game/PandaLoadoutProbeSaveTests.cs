using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED — the loadout SAVE ("copy the worn loadout into another row") pre-dispatch gates, result
/// parsing and unsaved-changes parsing.
///
/// <para><b>Origin:</b> owner-approved spec <c>docs/superpowers/specs/2026-09-26-loadout-copy-design.md</c>
/// (Discord feature request "Copy Loadout", Midokuni 2026-09-17). Measured on the owner's MAIN client
/// (2026-09-26, probe <c>Stellar.LoadoutSaveProbe</c>): <c>SaveProject</c> accepts a NON-worn plan id and
/// stores the LIVE setup into it. The gates pinned here keep the copy from ever sending a save the feature
/// did not intend: onto the worn plan itself, onto an id that is not a saved plan (incl. the synthesized
/// <c>-1</c> "Current" entry), before the worn plan is known, or while a switch/save is still running
/// (saving mid-switch would store a half-applied setup).</para>
/// </summary>
public sealed class PandaLoadoutProbeSaveTests
{
    private const int Unknown = PandaLoadoutProbe.UnknownPlanId;

    // ── DecideSave (pure) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_different_saved_loadout_with_nothing_in_flight_dispatches()
        => Assert.Equal(PandaLoadoutProbe.SaveDecision.Dispatch,
            PandaLoadoutProbe.DecideSave(4, 3, targetKnown: true, switchInFlight: false, saveInFlight: false));

    [Fact]
    public void The_worn_loadout_is_never_a_copy_target()
        => Assert.Equal(PandaLoadoutProbe.SaveDecision.TargetIsWorn,
            PandaLoadoutProbe.DecideSave(4, 4, targetKnown: true, switchInFlight: false, saveInFlight: false));

    [Fact]
    public void An_unknown_worn_loadout_refuses_before_anything_else()
    {
        // Contrast DecideSwitch, where unknown-current DISPATCHES: a switch to the worn plan is harmless,
        // but a save aimed at what might be the worn plan is not what the copy feature means.
        Assert.Equal(PandaLoadoutProbe.SaveDecision.CurrentUnknown,
            PandaLoadoutProbe.DecideSave(Unknown, 3, targetKnown: true, switchInFlight: true, saveInFlight: true));
    }

    [Fact]
    public void A_target_that_is_not_a_saved_loadout_is_refused()
        => Assert.Equal(PandaLoadoutProbe.SaveDecision.NoSuchTarget,
            PandaLoadoutProbe.DecideSave(4, -1, targetKnown: false, switchInFlight: false, saveInFlight: false));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_switch_or_save_in_flight_refuses(bool switchInFlight, bool saveInFlight)
        => Assert.Equal(PandaLoadoutProbe.SaveDecision.Busy,
            PandaLoadoutProbe.DecideSave(4, 3, targetKnown: true, switchInFlight, saveInFlight));

    [Fact]
    public void Each_refusal_maps_to_its_documented_result()
    {
        Assert.Equal(LoadoutResult.GameApiUnavailable, PandaLoadoutProbe.RefusalResult(PandaLoadoutProbe.SaveDecision.CurrentUnknown));
        Assert.Equal(LoadoutResult.Rejected, PandaLoadoutProbe.RefusalResult(PandaLoadoutProbe.SaveDecision.TargetIsWorn));
        Assert.Equal(LoadoutResult.NoSuchLoadout, PandaLoadoutProbe.RefusalResult(PandaLoadoutProbe.SaveDecision.NoSuchTarget));
        Assert.Equal(LoadoutResult.Rejected, PandaLoadoutProbe.RefusalResult(PandaLoadoutProbe.SaveDecision.Busy));
    }

    // ── Result + flag parsing (pure) ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("true", LoadoutResult.Success)]
    [InlineData("false", LoadoutResult.Rejected)]
    public void The_wrappers_bool_is_the_completion_signal(string raw, LoadoutResult expected)
        => Assert.Equal(expected, PandaLoadoutProbe.ParseSaveResult(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nil")]
    public void No_answer_yet_keeps_waiting(string? raw)
        => Assert.Null(PandaLoadoutProbe.ParseSaveResult(raw));

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void The_unsaved_check_parses_the_games_bool(string raw, bool expected)
        => Assert.Equal(expected, PandaLoadoutProbe.ParseUnsavedFlag(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("E")]
    [InlineData("E:attempt to index a nil value")]
    public void A_failed_unsaved_check_is_no_signal(string? raw)
        => Assert.Null(PandaLoadoutProbe.ParseUnsavedFlag(raw));

    // ── The UNSAVED row rides the two existing chunks (perf review: no extra DoString per merge) ──

    private static string ChunkConst(string name)
    {
        var f = typeof(PandaLoadoutProbe).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        return (string)f!.GetRawConstantValue()!;
    }

    [Fact]
    public void The_unsaved_check_is_a_row_of_the_merge_chunk_and_the_refresh_chunk()
    {
        Assert.Contains(PandaLoadoutProbe.UnsavedRowFragment, ChunkConst("LiveStateChunk"));
        var refresh = ChunkConst("RefreshChunk");
        Assert.Contains(PandaLoadoutProbe.UnsavedRowFragment, refresh);
        // After SyncProjectList, so it compares against FRESH saved data.
        Assert.True(refresh.IndexOf("AsyncGetRolePlanData", StringComparison.Ordinal)
                    < refresh.IndexOf(PandaLoadoutProbe.UnsavedRowFragment, StringComparison.Ordinal));
        // Scoped + guarded: its locals never leak, a failure never breaks the host chunk.
        Assert.StartsWith(" do ", PandaLoadoutProbe.UnsavedRowFragment);
        Assert.Contains("pcall(", PandaLoadoutProbe.UnsavedRowFragment);
    }

    [Theory]
    [InlineData("RES\t1\nLIVE\t1:2\t\t9\t0\t\nUNSAVED\t1", "1")]
    [InlineData("CUR=4\n4\tIci-LF\t2\t0\t\t\t\nUNSAVED\t0", "0")]
    [InlineData("RES\t\nUNSAVED\tE:attempt to index a nil value", "E:attempt to index a nil value")]
    [InlineData("RES\t\nLIVE\t\t\t9\t0\t", null)]   // an old dump without the row → no signal
    public void FindUnsavedRow_reads_the_row_value(string raw, string? expected)
        => Assert.Equal(expected, PandaLoadoutProbe.FindUnsavedRow(raw));

    [Fact]
    public void The_UNSAVED_row_is_invisible_to_the_plan_and_live_parsers()
    {
        // Regression guard for the pinned read paths: adding a row must not create a plan or a LIVE row.
        const string dump = "CUR=4\n4\tIci-LF\t2\t0\t\t\t\nUNSAVED\t1";
        var (current, plans) = PandaLoadoutProbe.ParseLoadoutData(dump);
        Assert.Equal(4, current);
        Assert.Single(plans);
        Assert.False(PandaLoadoutProbe.HasLiveRow("UNSAVED\t1"));
    }

    [Fact]
    public void The_save_chunk_drives_the_games_own_wrapper_not_the_raw_rpc()
    {
        var chunk = PandaLoadoutProbe.BuildSaveChunk(3);
        Assert.Contains("Z.VMMgr.GetVM(\"weapon\").AsyncSaveRolePlan(3, ZUtil.ZCancelSource.NeverCancelToken)", chunk);
        Assert.StartsWith("(Z.CoroUtil.create_coro_xpcall(function()", chunk);
        Assert.DoesNotContain("SaveProject", chunk);
    }

    // ── The gates are WIRED into CallSaveAsync (bridge forced resolved; refusals return before Lua) ──

    private sealed class FakeTypeRegistry : IGameTypeRegistry
    {
        public Type? FindType(string fullName) => null;
    }

    private static PandaLoadoutProbe Probe(int worn, params int[] planIds)
    {
        var probe = new PandaLoadoutProbe(new StubLog(), new FakeTypeRegistry());
        SetField(probe, "_bridgeResolved", true);
        SetField(probe, "_liveCurrentPlanId", worn);
        SetField(probe, "_knownPlanIds", planIds);
        return probe;
    }

    private static void SetField(PandaLoadoutProbe probe, string name, object value)
    {
        var f = typeof(PandaLoadoutProbe).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(f);
        f!.SetValue(probe, value);
    }

    private static bool HasPendingSave(PandaLoadoutProbe probe)
        => typeof(PandaLoadoutProbe).GetField("_pendingSave", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(probe) is not null;

    [Fact]
    public async Task Saving_onto_the_worn_loadout_completes_Rejected_without_queueing()
    {
        var probe = Probe(4, 3, 4);
        var task = ((ILoadoutSaveProbe)probe).CallSaveAsync(4, CancellationToken.None);
        Assert.True(task.IsCompleted);
        Assert.Equal(LoadoutResult.Rejected, await task);
        Assert.False(HasPendingSave(probe));
    }

    [Fact]
    public async Task Saving_onto_an_unknown_id_completes_NoSuchLoadout_without_queueing()
    {
        var probe = Probe(4, 3, 4);
        var task = ((ILoadoutSaveProbe)probe).CallSaveAsync(9, CancellationToken.None);
        Assert.True(task.IsCompleted);
        Assert.Equal(LoadoutResult.NoSuchLoadout, await task);
        Assert.False(HasPendingSave(probe));
    }

    [Fact]
    public async Task A_valid_save_queues_exactly_one_and_a_second_is_refused_as_busy()
    {
        var probe = Probe(4, 3, 4, 5);
        var first = ((ILoadoutSaveProbe)probe).CallSaveAsync(3, CancellationToken.None);
        var second = ((ILoadoutSaveProbe)probe).CallSaveAsync(5, CancellationToken.None);
        Assert.False(first.IsCompleted);   // waits for the game's wrapper result on the main-thread drain
        Assert.True(HasPendingSave(probe));
        Assert.True(second.IsCompleted);
        Assert.Equal(LoadoutResult.Rejected, await second);
    }

    [Fact]
    public async Task A_save_is_refused_while_a_switch_is_in_flight()
    {
        var probe = Probe(4, 3, 4, 5);
        SetField(probe, "_lastSwitchDispatchMs", 0L);
        var sw = ((ILoadoutProbe)probe).CallApplyAsync(5, CancellationToken.None);
        Assert.False(sw.IsCompleted);   // the switch is pending

        var save = ((ILoadoutSaveProbe)probe).CallSaveAsync(3, CancellationToken.None);
        Assert.True(save.IsCompleted);
        Assert.Equal(LoadoutResult.Rejected, await save);
        Assert.False(HasPendingSave(probe));
    }

    [Fact]
    public async Task An_already_cancelled_token_completes_Cancelled()
    {
        var probe = Probe(4, 3, 4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(LoadoutResult.Cancelled, await ((ILoadoutSaveProbe)probe).CallSaveAsync(3, cts.Token));
        Assert.False(HasPendingSave(probe));
    }

    [Fact]
    public async Task Logout_cancels_a_queued_save_and_clears_the_unsaved_flag()
    {
        var probe = Probe(4, 3, 4);
        SetField(probe, "_hasUnsavedChanges", true);
        var task = ((ILoadoutSaveProbe)probe).CallSaveAsync(3, CancellationToken.None);

        probe.ClearSession();

        Assert.True(task.IsCompleted);
        Assert.Equal(LoadoutResult.Cancelled, await task);
        Assert.False(probe.HasUnsavedChanges);
        Assert.False(HasPendingSave(probe));
    }

    // ── Application pass-through ──────────────────────────────────────────────────────────────────

    private sealed class FakeSaveProbe : ILoadoutSaveProbe
    {
        public int LastIndex = int.MinValue;
        public bool HasUnsavedChanges { get; set; }
        public Task<LoadoutResult> CallSaveAsync(int index, CancellationToken ct)
        {
            LastIndex = index;
            return Task.FromResult(LoadoutResult.Success);
        }
    }

    [Fact]
    public async Task LoadoutSaveService_passes_through_to_the_probe()
    {
        var fake = new FakeSaveProbe { HasUnsavedChanges = true };
        var svc = new LoadoutSaveService(fake);
        Assert.True(svc.HasUnsavedChanges);
        Assert.Equal(LoadoutResult.Success, await svc.SaveCurrentToAsync(7));
        Assert.Equal(7, fake.LastIndex);
    }
}
