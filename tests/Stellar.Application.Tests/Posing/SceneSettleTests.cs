using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game.Posing;
using Xunit;

namespace Stellar.Application.Tests.Posing;

/// <summary>Task 6 carry-overs: no posing probe in the ~2 s after a scene change (docs/il2cpp-probing-safety.md § How to
/// gate it #2), while a release (close / unfreeze) always reaches the model; and <c>loaded</c> runs exactly once.</summary>
public sealed class SceneSettleTests
{
    private long _now = 1_000;
    private SceneSettleWindow Window() => new(() => _now, spanTicks: 200);

    [Fact]
    public void Not_settling_before_any_scene_change()
    {
        Assert.False(Window().Settling);
    }

    [Fact]
    public void Settling_for_the_span_after_arm_then_clear()
    {
        var w = Window();
        w.Arm();
        Assert.True(w.Settling);
        _now += 199;
        Assert.True(w.Settling);
        _now += 1;
        Assert.False(w.Settling);
    }

    [Fact]
    public void A_second_scene_change_restarts_the_window()
    {
        var w = Window();
        w.Arm();
        _now += 150;
        w.Arm();   // leave, then enter
        _now += 150;
        Assert.True(w.Settling);
        _now += 50;
        Assert.False(w.Settling);
    }

    [Fact]
    public void Settling_model_reads_no_position_and_ignores_controls()
    {
        var w = Window();
        var inner = new FakePoseModel();
        var m = new SettledPoseModel(inner, w);
        w.Arm();
        Assert.Null(m.Position);
        Assert.False(m.PlayAction(5));
        Assert.Equal(-1f, m.ReadMoment());
        m.SetMoment(0.5f);
        m.SetExpression(null, true);
        m.SetLook(LookPart.Head, LookMode.Lens, false);
        m.Aim(LookPart.Eyes, 0.1f, 0.2f);
        m.SetYaw(30f);
        m.SetFrozen(true);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void Settling_model_still_releases()
    {
        var w = Window();
        var inner = new FakePoseModel();
        var m = new SettledPoseModel(inner, w);
        w.Arm();
        m.SetFrozen(false);
        m.Close(PoseTouches.Action | PoseTouches.Yaw);
        Assert.Equal(new List<string> { "frozen False", "close Action, Yaw" }, inner.Calls);
    }

    [Fact]
    public void Settled_model_forwards_everything()
    {
        var w = Window();
        var inner = new FakePoseModel();
        var m = new SettledPoseModel(inner, w);
        Assert.Equal(inner.Visible, m.Position);
        Assert.True(m.PlayAction(5));
        Assert.Equal(0.25f, m.ReadMoment());
        m.SetYaw(30f);
        m.SetFrozen(true);
        Assert.Equal(new List<string> { "play 5", "read", "yaw 30", "frozen True" }, inner.Calls);
    }

    [Fact]
    public void Closed_model_is_never_read_again()
    {
        var inner = new FakePoseModel();
        var m = new SettledPoseModel(inner, Window());
        m.Close(PoseTouches.None);
        m.Close(PoseTouches.None);
        Assert.Null(m.Position);
        Assert.False(m.PlayAction(5));
        m.SetFrozen(false);
        Assert.Equal(1, inner.CloseCount);
        Assert.Equal(new List<string> { "close None" }, inner.Calls);
    }

    [Fact]
    public void Loaded_reports_exactly_once()
    {
        var seen = new List<bool>();
        var once = new LoadedOnce(seen.Add);
        Assert.False(once.Reported);
        once.Report(true);
        once.Report(false);
        Assert.True(once.Reported);
        Assert.Equal(new List<bool> { true }, seen);
    }
}
