using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public sealed class HotkeyServiceTests
{
    [Fact]
    public void DeclareAction_NoCollision_ResolvesToSuggestedDefault()
    {
        var input = new FakeInputGateway();
        var svc = new HotkeyService(input, new NullLog());

        var handle = svc.DeclareAction(
            new HotkeyAction("a", "test", new KeyBinding(StellarKeyCode.F1)),
            callback: () => { });

        Assert.Equal(new KeyBinding(StellarKeyCode.F1), handle.CurrentBinding);
    }

    [Fact]
    public void DeclareAction_NullDefault_ResolvesToNull()
    {
        var svc = new HotkeyService(new FakeInputGateway(), new NullLog());

        var handle = svc.DeclareAction(
            new HotkeyAction("a", "test", SuggestedDefault: null),
            callback: () => { });

        Assert.Null(handle.CurrentBinding);
    }

    [Fact]
    public void Collision_AlphabeticallyFirstActionWinsBinding()
    {
        var svc = new HotkeyService(new FakeInputGateway(), new NullLog());
        var bindF12 = new KeyBinding(StellarKeyCode.F12);

        var first  = svc.DeclareAction(new HotkeyAction("autonav.toggle",   "AutoNav",   bindF12), () => { });
        var second = svc.DeclareAction(new HotkeyAction("chattools.toggle", "ChatTools", bindF12), () => { });

        Assert.Equal(bindF12, first.CurrentBinding);
        Assert.Null(second.CurrentBinding);
    }

    [Fact]
    public void Press_BoundKey_InvokesCallback()
    {
        var input = new FakeInputGateway();
        var svc = new HotkeyService(input, new NullLog());
        var hits = 0;

        svc.DeclareAction(
            new HotkeyAction("a", "t", new KeyBinding(StellarKeyCode.F11)),
            () => hits++);

        input.Press(StellarKeyCode.F11);
        svc.Tick();

        Assert.Equal(1, hits);
    }

    [Fact]
    public void Press_KeyWithoutModifier_DoesNotMatchActionRequiringModifier()
    {
        var input = new FakeInputGateway();
        var svc = new HotkeyService(input, new NullLog());
        var hits = 0;

        svc.DeclareAction(
            new HotkeyAction("a", "t", new KeyBinding(StellarKeyCode.F12, ModifierKeys.Shift)),
            () => hits++);

        input.Press(StellarKeyCode.F12);  // no shift
        svc.Tick();

        Assert.Equal(0, hits);
    }

    [Fact]
    public void Press_WithCorrectModifier_InvokesCallback()
    {
        var input = new FakeInputGateway();
        var svc = new HotkeyService(input, new NullLog());
        var hits = 0;

        svc.DeclareAction(
            new HotkeyAction("a", "t", new KeyBinding(StellarKeyCode.F12, ModifierKeys.Shift)),
            () => hits++);

        input.SetModifiers(ModifierKeys.Shift);
        input.Press(StellarKeyCode.F12);
        svc.Tick();

        Assert.Equal(1, hits);
    }

    [Fact]
    public void Press_UnboundAction_DoesNotInvokeCallback()
    {
        var input = new FakeInputGateway();
        var svc = new HotkeyService(input, new NullLog());
        var hits = 0;

        // First action wins F1; second has no default and remains unbound.
        svc.DeclareAction(new HotkeyAction("a", "t", new KeyBinding(StellarKeyCode.F1)), () => { });
        svc.DeclareAction(new HotkeyAction("b", "t", SuggestedDefault: null),            () => hits++);

        input.Press(StellarKeyCode.F1);
        svc.Tick();

        Assert.Equal(0, hits);
    }

    [Fact]
    public void Callback_Throws_LoggedRateLimited_OtherCallbacksUnaffected()
    {
        var input = new FakeInputGateway();
        var log = new RecordingLog();
        var svc = new HotkeyService(input, log);
        var goodHits = 0;

        svc.DeclareAction(new HotkeyAction("bad", "t", new KeyBinding(StellarKeyCode.F1)),
            () => throw new System.InvalidOperationException("boom"));
        svc.DeclareAction(new HotkeyAction("good", "t", new KeyBinding(StellarKeyCode.F2)),
            () => goodHits++);

        input.Press(StellarKeyCode.F1);
        input.Press(StellarKeyCode.F2);
        svc.Tick();

        Assert.Equal(1, goodHits);                                  // good action ran
        Assert.Single(log.Errors, e => e.Contains("bad"));           // bad logged once
    }

    [Fact]
    public void DisposedHandle_DoesNotReceiveCallbacks()
    {
        var input = new FakeInputGateway();
        var svc = new HotkeyService(input, new NullLog());
        var hits = 0;

        var handle = svc.DeclareAction(
            new HotkeyAction("a", "t", new KeyBinding(StellarKeyCode.F1)),
            () => hits++);

        handle.Dispose();
        input.Press(StellarKeyCode.F1);
        svc.Tick();

        Assert.Equal(0, hits);
    }

    [Fact]
    public void NoKeyPressed_NoCallbacks()
    {
        var input = new FakeInputGateway();
        var svc = new HotkeyService(input, new NullLog());
        var hits = 0;

        svc.DeclareAction(new HotkeyAction("a", "t", new KeyBinding(StellarKeyCode.F1)),
            () => hits++);

        svc.Tick();

        Assert.Equal(0, hits);
    }

    [Fact]
    public void Collision_BothLoggedOnce()
    {
        var log = new RecordingLog();
        var svc = new HotkeyService(new FakeInputGateway(), log);
        var bind = new KeyBinding(StellarKeyCode.F12);

        svc.DeclareAction(new HotkeyAction("autonav.toggle",   "A", bind), () => { });
        svc.DeclareAction(new HotkeyAction("chattools.toggle", "C", bind), () => { });

        Assert.Single(log.Warnings, w => w.Contains("F12") && w.Contains("chattools.toggle"));
    }

    [Fact]
    public void DeclareTwice_SameId_SecondCallReturnsTheSameHandle()
    {
        var svc = new HotkeyService(new FakeInputGateway(), new NullLog());
        var act = new HotkeyAction("a", "t", new KeyBinding(StellarKeyCode.F1));

        var h1 = svc.DeclareAction(act, () => { });
        var h2 = svc.DeclareAction(act, () => { });

        // Duplicate declarations should be a logged warning, not a crash; the
        // second registration replaces the callback but reuses the handle.
        Assert.Same(h1, h2);
    }

    // Test doubles
    private sealed class FakeInputGateway : IInputGateway
    {
        private readonly List<StellarKeyCode> _pressed = new();
        public IReadOnlyList<StellarKeyCode> PressedKeysThisFrame => _pressed;
        public ModifierKeys CurrentModifiers { get; private set; }
        public Resolution CurrentResolution => new(1920, 1080);
        public (float X, float Y) PointerPosition => (0f, 0f);
        public bool LeftMouseDown => false;
        public bool LeftMousePressedSinceTick => false;
        public int CurrentFrame => 1;   // tests don't exercise frame-dedupe — Tick() takes no frame arg

        public void Press(StellarKeyCode key) => _pressed.Add(key);
        public void SetModifiers(ModifierKeys m) => CurrentModifiers = m;
        public void Clear() { _pressed.Clear(); CurrentModifiers = ModifierKeys.None; }
    }

    private sealed class NullLog : IPluginLog
    {
        public void Info(string message)    { }
        public void Warning(string message) { }
        public void Error(string message)   { }
        public void Debug(string message)   { }
    }

    private sealed class RecordingLog : IPluginLog
    {
        public List<string> Infos    { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Errors   { get; } = new();
        public List<string> Debugs   { get; } = new();
        public void Info(string message)    => Infos.Add(message);
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message)   => Errors.Add(message);
        public void Debug(string message)   => Debugs.Add(message);
    }
}
