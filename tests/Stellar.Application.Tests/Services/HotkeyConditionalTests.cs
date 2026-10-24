using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

// Pins HotkeyAction.IsActive: a gated action is dormant while its gate is false — no callback AND its chord is not in the
// game-side block set — and ungated actions keep today's behaviour (always dispatched, always blocked under block-all).
public sealed class HotkeyConditionalTests
{
    private static readonly KeyBinding Tab = new(StellarKeyCode.Tab);
    private static readonly KeyBinding F12 = new(StellarKeyCode.F12);

    private sealed class Rig
    {
        public readonly FakeInputGateway Input = new();
        public List<KeyBinding> Blocked = new();
        public readonly HotkeyService Svc;
        // No config section → block-all defaults ON (LoadStoredBlockAll), the shipped default.
        public Rig() => Svc = new HotkeyService(Input, new NullLog(), null, keys => Blocked = keys.ToList());
        public void Press(StellarKeyCode k) { Input.Clear(); Input.Press(k); Svc.Tick(); Input.Clear(); }
    }

    [Fact]
    public void GateFalse_DoesNotFire_AndIsNotBlocked()
    {
        var rig = new Rig();
        int fired = 0;
        rig.Svc.DeclareAction(new HotkeyAction("gated", "g", Tab) { IsActive = () => false }, () => fired++);

        rig.Press(StellarKeyCode.Tab);

        Assert.Equal(0, fired);
        Assert.DoesNotContain(Tab, rig.Blocked);
    }

    [Fact]
    public void GateTrue_FiresAndBlocks_ThenFlipFalse_Unblocks()
    {
        var rig = new Rig();
        bool active = true;
        int fired = 0;
        rig.Svc.DeclareAction(new HotkeyAction("gated", "g", Tab) { IsActive = () => active }, () => fired++);

        rig.Svc.Tick();   // a tick with no press latches the gate → block set rebuilt before any press
        Assert.Contains(Tab, rig.Blocked);
        rig.Press(StellarKeyCode.Tab);
        Assert.Equal(1, fired);

        active = false;
        rig.Svc.Tick();
        Assert.DoesNotContain(Tab, rig.Blocked);
        rig.Press(StellarKeyCode.Tab);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void UngatedAction_Unchanged_BlockedAndFires()
    {
        var rig = new Rig();
        int fired = 0;
        rig.Svc.DeclareAction(new HotkeyAction("plain", "p", F12), () => fired++);
        rig.Svc.DeclareAction(new HotkeyAction("gated", "g", Tab) { IsActive = () => false }, () => { });

        rig.Press(StellarKeyCode.F12);

        Assert.Equal(1, fired);
        Assert.Contains(F12, rig.Blocked);
        Assert.DoesNotContain(Tab, rig.Blocked);
    }

    [Fact]
    public void ThrowingGate_CountsAsInactive()
    {
        var rig = new Rig();
        int fired = 0;
        rig.Svc.DeclareAction(new HotkeyAction("gated", "g", Tab) { IsActive = () => throw new InvalidOperationException() }, () => fired++);

        rig.Press(StellarKeyCode.Tab);

        Assert.Equal(0, fired);
        Assert.DoesNotContain(Tab, rig.Blocked);
    }

    private sealed class FakeInputGateway : IInputGateway
    {
        private readonly List<StellarKeyCode> _pressed = new();
        public IReadOnlyList<StellarKeyCode> PressedKeysThisFrame => _pressed;
        public ModifierKeys CurrentModifiers => ModifierKeys.None;
        public bool IsKeyHeld(StellarKeyCode key) => false;
        public Resolution CurrentResolution => new(1920, 1080);
        public (float X, float Y) PointerPosition => (0f, 0f);
        public bool LeftMouseDown => false;
        public bool LeftMousePressedSinceTick => false;
        public int CurrentFrame => 1;
        public void Press(StellarKeyCode key) => _pressed.Add(key);
        public void Clear() => _pressed.Clear();
    }

    private sealed class NullLog : IPluginLog
    {
        public void Info(string message)    { }
        public void Warning(string message) { }
        public void Error(string message)   { }
        public void Debug(string message)   { }
    }
}
