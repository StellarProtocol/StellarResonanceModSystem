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
        public readonly List<VisibilityLayers> Calls = new();
        public VisibilityLayers Apply(VisibilityLayers requested)
        {
            Calls.Add(requested);
            return requested & ~Unsupported;
        }
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
}
