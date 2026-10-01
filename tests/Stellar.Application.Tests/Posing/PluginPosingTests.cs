using Stellar.Abstractions.Domain;
using Stellar.Application.Hosting;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Spec § 5 "per-plugin facade, unload backstop": a plugin's unload resets only its own people and drops its handlers.
public sealed class PluginPosingTests
{
    [Fact]
    public void ReleaseAll_resets_only_this_plugins_people_and_drops_its_handlers()
    {
        var r = new PosingRig();
        var a = new PluginPosing(r.Svc, new object());
        var b = new PluginPosing(r.Svc, new object());
        var raised = 0;
        a.Changed += () => raised++;
        var mine = a.Select(new EntityId(2))!;
        var theirs = b.Select(new EntityId(1))!;
        mine.Yaw = 10f;
        theirs.Yaw = 5f;
        a.ReleaseAll();
        Assert.Equal(PoseTargetState.Released, mine.State);
        Assert.Equal(PoseTargetState.Ready, theirs.State);
        var before = raised;
        theirs.Reset();   // raises Changed on the service — a's handler is gone
        Assert.Equal(before, raised);
    }

    [Fact]
    public void A_plugin_cannot_take_a_person_another_plugin_is_posing()
    {
        var r = new PosingRig();
        var a = new PluginPosing(r.Svc, new object());
        var b = new PluginPosing(r.Svc, new object());
        Assert.NotNull(a.Select(new EntityId(2)));
        Assert.Null(b.Select(new EntityId(2)));
    }

    [Fact]
    public void A_plugins_ResetAll_resets_only_its_own_people()
    {
        var r = new PosingRig();
        var a = new PluginPosing(r.Svc, new object());
        var b = new PluginPosing(r.Svc, new object());
        var mine = a.Select(new EntityId(2))!;
        var theirs = b.Select(new EntityId(1))!;
        mine.Yaw = 1f;
        theirs.Yaw = 1f;
        a.ResetAll();
        Assert.Equal(PoseTargetState.Released, mine.State);
        Assert.Equal(PoseTargetState.Ready, theirs.State);
    }

    [Fact]
    public void The_free_camera_scope_releases_posing_on_unload()
    {
        var r = new PosingRig();
        var posing = new PluginPosing(r.Svc, new object());
        var scope = new FreeCameraScope(null, null, null, null, null, posing);
        var t = posing.Select(new EntityId(2))!;
        t.Yaw = 3f;
        scope.ReleaseAll();
        Assert.Equal(PoseTargetState.Released, t.State);
        Assert.Equal(Stellar.Application.Abstractions.PoseTouches.Yaw, r.Backend.Model(2).Closed);
    }
}
