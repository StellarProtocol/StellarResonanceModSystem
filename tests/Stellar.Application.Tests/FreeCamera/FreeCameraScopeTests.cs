using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;
using static Stellar.Application.Tests.FreeCamera.CameraOverrideServiceTests;

namespace Stellar.Application.Tests.FreeCamera;

// Spec § 6 "the framework force-releases a plugin's handles on unload": one call releases camera, freeze and shield.
public sealed class FreeCameraScopeTests
{
    private sealed class NullShieldBackend : Stellar.Application.Abstractions.IInputShieldBackend
    {
        public bool On;
        public bool Apply(bool camera, bool pause) { On = camera; return true; }
        public void Forget() { }
    }

    private sealed class NullFreezeBackend : Stellar.Application.Abstractions.ISceneFreezeBackend
    {
        public bool Frozen;
        public event System.Action? HoldDisabled { add { } remove { } }
        public void EnsureHooks() { }
        public void FreezeAll(bool holdPositions) => Frozen = true;
        public void UnfreezeAll() => Frozen = false;
        public bool HoldsPositions => false;
    }

    [Fact]
    public void ReleaseAll_hands_back_camera_freeze_and_shield()
    {
        var cam = new CameraOverrideService(new FakeBackend(), new LookAtService(new FakeLookAt(), _ => { }), false, _ => { });
        var shieldBackend = new NullShieldBackend();
        var shield = new InputShieldService(shieldBackend, new NullReader(), new NoFocus(), _ => { });
        var freezeBackend = new NullFreezeBackend();
        var freeze = new SceneFreezeService(freezeBackend, false);
        var scope = new FreeCameraScope(new PluginCameraOverride(cam, new object()), new PluginInputShield(shield, new object()),
            new PluginSceneFreeze(freeze, new object()), null, null);
        scope.Camera!.TryAcquire(out var control);
        scope.Shield!.Shield();
        scope.Freeze!.Freeze();
        scope.ReleaseAll();
        Assert.False(control!.IsActive);
        Assert.False(shieldBackend.On);
        Assert.False(freezeBackend.Frozen);
    }

    private sealed class NullReader : Stellar.Application.Abstractions.IShieldInputReader
    {
        public bool IsHeld(Stellar.Abstractions.Domain.StellarKeyCode key) => false;
        public Stellar.Abstractions.Domain.ModifierKeys Modifiers => default;
        public bool IsMouseHeld(int button) => false;
        public (float X, float Y) MouseDelta => (0, 0);
        public float Wheel => 0;
        public (float X, float Y) Pointer => (0, 0);
        public bool PointerOverGameUi => false;
    }

    private sealed class NoFocus : Stellar.Application.Abstractions.ITextFieldFocus
    {
        public bool AnyFieldFocused => false;
    }
}
