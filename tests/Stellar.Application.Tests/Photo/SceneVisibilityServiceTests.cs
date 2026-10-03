using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Photo;

public sealed class SceneVisibilityServiceTests
{
    private sealed class FakeBackend : IVisibilityBackend
    {
        public VisibilityLayers Unsupported;
        public VisibilityLayers Available = (VisibilityLayers)31; // every defined bit, by default
        public readonly List<VisibilityLayers> Calls = new();
        public readonly List<VisibilityLayers> Reasserts = new();
        public VisibilityLayers Apply(VisibilityLayers requested)
        {
            Calls.Add(requested);
            return requested & ~Unsupported;
        }
        public VisibilityLayers Reassert(VisibilityLayers requested)
        {
            Reasserts.Add(requested);
            return requested & ~Unsupported;
        }
        VisibilityLayers IVisibilityBackend.Available => Available;
    }

    [Fact]
    public void Layer_stays_hidden_until_last_token_disposed()
    {
        var b = new FakeBackend();
        var s = new SceneVisibilityService(b);
        var a = s.Hide(VisibilityLayers.Nameplates);
        var c = s.Hide(VisibilityLayers.Nameplates);
        a.Dispose();
        Assert.Equal(VisibilityLayers.Nameplates, s.Hidden);
        c.Dispose();
        Assert.Equal(VisibilityLayers.None, s.Hidden);
    }

    [Fact]
    public void Double_dispose_is_a_noop()
    {
        var s = new SceneVisibilityService(new FakeBackend());
        var keep = s.Hide(VisibilityLayers.GameHud);
        var t = s.Hide(VisibilityLayers.GameHud);
        t.Dispose();
        t.Dispose();
        Assert.Equal(VisibilityLayers.GameHud, s.Hidden);
        keep.Dispose();
    }

    [Fact]
    public void KeepParty_only_when_every_OtherPlayers_token_keeps_party()
    {
        var b = new FakeBackend();
        var s = new SceneVisibilityService(b);
        var keep = s.Hide(VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty);
        Assert.Equal(VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty, b.Calls[^1]);
        var all = s.Hide(VisibilityLayers.OtherPlayers);
        Assert.Equal(VisibilityLayers.OtherPlayers, b.Calls[^1]);
        all.Dispose();
        Assert.Equal(VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty, b.Calls[^1]);
        keep.Dispose();
    }

    [Fact]
    public void Unsupported_layer_is_not_reported_hidden()
    {
        var b = new FakeBackend { Unsupported = VisibilityLayers.OtherPlayers };
        var s = new SceneVisibilityService(b);
        s.Hide(VisibilityLayers.OtherPlayers | VisibilityLayers.GameHud);
        Assert.Equal(VisibilityLayers.GameHud, s.Hidden);
    }

    [Fact]
    public void KeepParty_is_masked_out_when_backend_cannot_achieve_OtherPlayers()
    {
        // The backend can't hide OtherPlayers at all; it can only ever hand back the
        // KeepParty bit alone, which is meaningless without OtherPlayers actually hidden.
        var b = new FakeBackend { Unsupported = VisibilityLayers.OtherPlayers };
        var s = new SceneVisibilityService(b);
        s.Hide(VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty);
        Assert.Equal(VisibilityLayers.None, s.Hidden);
    }

    [Fact]
    public void Changed_fires_only_on_real_change()
    {
        var s = new SceneVisibilityService(new FakeBackend());
        var n = 0;
        s.Changed += _ => n++;
        var a = s.Hide(VisibilityLayers.GameHud);
        var b = s.Hide(VisibilityLayers.GameHud);
        b.Dispose();
        a.Dispose();
        Assert.Equal(2, n);
    }

    [Fact]
    public void Plugin_facade_releases_all_its_tokens()
    {
        var s = new SceneVisibilityService(new FakeBackend());
        var other = s.Hide(VisibilityLayers.GameHud);
        var p = new PluginSceneVisibility(s, owner: "plugin.a");
        p.Hide(VisibilityLayers.Nameplates);
        p.Hide(VisibilityLayers.GameHud);
        p.ReleaseAll();
        Assert.Equal(VisibilityLayers.GameHud, s.Hidden);
        other.Dispose();
        Assert.Equal(VisibilityLayers.None, s.Hidden);
    }

    // Tasks 1-5 review carry-over (b): an unloaded plugin must never be called back through Changed.
    [Fact]
    public void Plugin_facade_unsubscribes_its_changed_handlers_on_release()
    {
        var s = new SceneVisibilityService(new FakeBackend());
        var p = new PluginSceneVisibility(s, owner: "plugin.a");
        var calls = 0;
        p.Changed += _ => calls++;
        var t = p.Hide(VisibilityLayers.GameHud);
        Assert.Equal(1, calls);
        p.ReleaseAll();                         // releases the token without calling back the unloaded plugin
        Assert.Equal(1, calls);
        s.Hide(VisibilityLayers.Nameplates);    // later changes by others never reach it
        Assert.Equal(1, calls);
        t.Dispose();
    }

    [Fact]
    public void Plugin_facade_remove_detaches_a_handler()
    {
        var s = new SceneVisibilityService(new FakeBackend());
        var p = new PluginSceneVisibility(s, owner: "plugin.a");
        var calls = 0;
        void H(VisibilityLayers _) => calls++;
        p.Changed += H;
        p.Changed -= H;
        s.Hide(VisibilityLayers.GameHud);
        Assert.Equal(0, calls);
    }

    // Fix round 1 (#2/#4): the game's own camera mode and rebuilt targets (canvas, ZUiRoot) can undo a held hide;
    // the host calls Reassert on those signals and the held set is re-issued.
    [Fact]
    public void Reassert_reissues_the_held_set()
    {
        var b = new FakeBackend();
        var s = new SceneVisibilityService(b);
        using var t = s.Hide(VisibilityLayers.Nameplates | VisibilityLayers.OtherPlayers);
        s.Reassert();
        Assert.Equal(new[] { VisibilityLayers.Nameplates | VisibilityLayers.OtherPlayers }, b.Reasserts);
    }

    [Fact]
    public void Reassert_with_nothing_held_touches_nothing()
    {
        var b = new FakeBackend();
        var s = new SceneVisibilityService(b);
        s.Hide(VisibilityLayers.GameHud).Dispose();
        s.Reassert();
        Assert.Empty(b.Reasserts);
    }

    [Fact]
    public void Reassert_updates_hidden_when_a_layer_becomes_achievable()
    {
        var b = new FakeBackend { Unsupported = VisibilityLayers.GameHud };
        var s = new SceneVisibilityService(b);
        var seen = new List<VisibilityLayers>();
        s.Changed += seen.Add;
        using var t = s.Hide(VisibilityLayers.GameHud);
        Assert.Equal(VisibilityLayers.None, s.Hidden);
        b.Unsupported = VisibilityLayers.None;   // e.g. ZUiRoot now exists
        s.Reassert();
        Assert.Equal(VisibilityLayers.GameHud, s.Hidden);
        Assert.Equal(new[] { VisibilityLayers.GameHud }, seen);
    }

    // Task: ISceneVisibility.Available — sourced from the backend probe, forwarded verbatim (no recomputation).
    [Fact]
    public void Available_forwards_the_backends_probe()
    {
        var b = new FakeBackend { Available = VisibilityLayers.GameHud | VisibilityLayers.Nameplates };
        var s = new SceneVisibilityService(b);
        Assert.Equal(VisibilityLayers.GameHud | VisibilityLayers.Nameplates, s.Available);
    }

    [Fact]
    public void Available_reflects_the_backend_live_not_a_snapshot()
    {
        var b = new FakeBackend { Available = VisibilityLayers.None };
        var s = new SceneVisibilityService(b);
        Assert.Equal(VisibilityLayers.None, s.Available);
        b.Available = VisibilityLayers.OtherPlayers; // e.g. the reflection target resolved after construction
        Assert.Equal(VisibilityLayers.OtherPlayers, s.Available);
    }

    [Fact]
    public void Available_is_independent_of_Hidden()
    {
        // A layer can be available (drivable) without being held hidden by anyone right now.
        var b = new FakeBackend { Available = VisibilityLayers.GameHud };
        var s = new SceneVisibilityService(b);
        Assert.Equal(VisibilityLayers.None, s.Hidden);
        Assert.Equal(VisibilityLayers.GameHud, s.Available);
    }

    [Fact]
    public void Plugin_facade_forwards_Available()
    {
        var b = new FakeBackend { Available = VisibilityLayers.Nameplates };
        var s = new SceneVisibilityService(b);
        var p = new PluginSceneVisibility(s, owner: "plugin.a");
        Assert.Equal(VisibilityLayers.Nameplates, p.Available);
        b.Available = VisibilityLayers.None;
        Assert.Equal(VisibilityLayers.None, p.Available);
    }
}
