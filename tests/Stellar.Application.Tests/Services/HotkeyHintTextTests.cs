using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.SettingsPanels;
using Xunit;

namespace Stellar.Application.Tests.Services;

// Settings hints that name a framework hotkey ("{0} opens the Stellar menu.") must show the LIVE binding, formatted
// like the Hotkeys pill, follow a rebind at once, and switch to the no-key line when the key is cleared.
public sealed class HotkeyHintTextTests
{
    private const string Id = "framework.settings-toggle";

    private static (HotkeyService svc, HotkeyHintText text, FormatLoc loc) Rig(KeyBinding? binding)
    {
        var svc = new HotkeyService(new FakeInputGateway(), new NullLog());
        svc.DeclareAction(new HotkeyAction(Id, "Toggle", binding), () => { });
        var loc = new FormatLoc();
        return (svc, new HotkeyHintText(svc, loc), loc);
    }

    [Fact]
    public void Bound_FormatsTheTemplateWithThePillText()
    {
        var (_, text, _) = Rig(new KeyBinding(StellarKeyCode.Home, ModifierKeys.Shift));
        Assert.Equal("about[Shift+Home]", text.Text(Id, "about", "about.unbound"));
        Assert.Equal("Shift+Home", text.BindingLabel(Id, "notSet"));
    }

    [Fact]
    public void Rebind_ShowsTheNewKeyAtOnce()
    {
        var (svc, text, _) = Rig(new KeyBinding(StellarKeyCode.Home, ModifierKeys.Shift));
        text.Text(Id, "about", "about.unbound");   // cached

        ((IHotkeyDirectory)svc).Rebind(Id, new KeyBinding(StellarKeyCode.F5, ModifierKeys.Ctrl));

        Assert.Equal("about[Ctrl+F5]", text.Text(Id, "about", "about.unbound"));
        Assert.Equal("Ctrl+F5", text.BindingLabel(Id, "notSet"));
    }

    [Fact]
    public void Unbound_UsesTheNoKeyLine()
    {
        var (svc, text, _) = Rig(new KeyBinding(StellarKeyCode.Home, ModifierKeys.Shift));
        ((IHotkeyDirectory)svc).Rebind(Id, null);

        Assert.Equal("about.unbound", text.Text(Id, "about", "about.unbound"));
        Assert.Equal("notSet", text.BindingLabel(Id, "notSet"));
    }

    [Fact]
    public void NotDeclaredYet_IsNotCached_SoTheLaterDeclarationShows()
    {
        var svc = new HotkeyService(new FakeInputGateway(), new NullLog());
        var text = new HotkeyHintText(svc, new FormatLoc());
        Assert.Equal("about.unbound", text.Text(Id, "about", "about.unbound"));

        svc.DeclareAction(new HotkeyAction(Id, "Toggle", new KeyBinding(StellarKeyCode.Home, ModifierKeys.Shift)), () => { });

        Assert.Equal("about[Shift+Home]", text.Text(Id, "about", "about.unbound"));
    }

    [Fact]
    public void LanguageSwitch_DropsTheCache()
    {
        var (_, text, loc) = Rig(new KeyBinding(StellarKeyCode.Home, ModifierKeys.Shift));
        text.Text(Id, "about", "about.unbound");

        loc.Prefix = "ja:";
        loc.RaiseChanged();

        Assert.Equal("ja:about[Shift+Home]", text.Text(Id, "about", "about.unbound"));
    }

    private sealed class FormatLoc : ILocalization
    {
        public string Prefix = "";
        public string Language => "en";
        public event Action? LanguageChanged;
        public string T(string key) => Prefix + key;
        public string TFormat(string key, params object[] args) => Prefix + key + "[" + string.Join(",", args) + "]";
        public void RaiseChanged() => LanguageChanged?.Invoke();
    }

    private sealed class FakeInputGateway : IInputGateway
    {
        public IReadOnlyList<StellarKeyCode> PressedKeysThisFrame { get; } = new List<StellarKeyCode>();
        public ModifierKeys CurrentModifiers => ModifierKeys.None;
        public bool IsKeyHeld(StellarKeyCode key) => false;
        public Resolution CurrentResolution => new(1920, 1080);
        public (float X, float Y) PointerPosition => (0f, 0f);
        public bool LeftMouseDown => false;
        public bool LeftMousePressedSinceTick => false;
        public int CurrentFrame => 1;
    }

    private sealed class NullLog : IPluginLog
    {
        public void Info(string message)    { }
        public void Warning(string message) { }
        public void Error(string message)   { }
        public void Debug(string message)   { }
    }
}
