using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Fix round 1 (#8): leaving the scene / logging out while in the game's photo mode never fires its exit hook,
// so the probe must reset its latches and report None.
public sealed class PandaPhotoModeProbeTests
{
    private sealed class NoTypes : Stellar.Application.Abstractions.IGameTypeRegistry
    {
        public System.Type? FindType(string fullName) => null;
    }

    private sealed class NullLog : Stellar.Abstractions.Services.IPluginLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }

    [Fact]
    public void Scene_leave_resets_to_none()
    {
        var cs = new ClientStateService();
        var p = new PandaPhotoModeProbe(new NoTypes(), cs, new NullLog());
        var kinds = new List<PhotoModeKind>();
        p.KindChanged += kinds.Add;
        p.OnCameraMode(true);
        p.OnSelfie(true);
        cs.RaiseSceneChanged("8");
        cs.RaiseSceneChanged(null);
        Assert.Equal(new[] { PhotoModeKind.CameraFrame, PhotoModeKind.Selfie, PhotoModeKind.None }, kinds);
        p.OnCameraMode(true);
        Assert.Equal(PhotoModeKind.CameraFrame, kinds[^1]);   // selfie latch was cleared too
    }

    [Fact]
    public void Logout_resets_to_none()
    {
        var cs = new ClientStateService();
        cs.RaiseLogin();
        var p = new PandaPhotoModeProbe(new NoTypes(), cs, new NullLog());
        var kinds = new List<PhotoModeKind>();
        p.KindChanged += kinds.Add;
        p.OnCameraMode(true);
        cs.RaiseLogout();
        Assert.Equal(PhotoModeKind.None, kinds[^1]);
    }
}
