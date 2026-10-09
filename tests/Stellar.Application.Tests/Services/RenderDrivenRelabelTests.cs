using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.SettingsPanels;
using Xunit;

namespace Stellar.Application.Tests.Services;

// Pins WHEN the framework reads the language for its own Settings text: never while wiring (pre-Game.Init — a
// first client-language read then can latch the wrong language for the session), only when the text is drawn
// or on an explicit language switch. HotkeysPanel itself is not constructible here (its capture partial loads
// UnityEngine.CoreModule), so the trigger lives in FrameworkHotkeyRelabeler, which the panel builds in its ctor and
// calls only from its RowLabel Func.
public sealed class RenderDrivenRelabelTests
{
    private sealed class CountingLocalization : ILocalization
    {
        public int Calls;
        public string Language => "ko";
        public event Action? LanguageChanged;
        public string T(string key) { Calls++; return "ko:" + key; }
        public string TFormat(string key, params object[] args) { Calls++; return "ko:" + key; }
        public void RaiseChanged() => LanguageChanged?.Invoke();
    }

    private static HotkeyService FrameworkHotkeys()
    {
        var svc = new HotkeyService(new NullInput(), new NullLog());
        foreach (var id in FrameworkHotkeyLabels.DescriptionKeys.Keys)
            svc.DeclareAction(new HotkeyAction(id, "English", null), () => { });
        return svc;
    }

    [Fact]
    public void SettingsHubSpec_Create_ReadsNoLanguage_TitleResolvesOnlyWhenDisplayed()
    {
        var loc = new CountingLocalization();

        var spec = SettingsHubSpec.Create(loc, () => true);

        Assert.Equal(0, loc.Calls);
        Assert.Equal("Stellar Settings", spec.Title);
        Assert.False(spec.StartVisible);
        Assert.Equal("ko:settings.window.title", spec.DisplayTitle);   // the chrome's read, at build
        Assert.Equal(1, loc.Calls);
    }

    [Fact]
    public void WindowSpec_DisplayTitle_FallsBackToTitle_WithoutProvider()
    {
        var spec = new WindowSpec("x", "Plain", new WindowRect(0, 0, 1, 1), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { ShouldRender = () => true };
        Assert.Equal("Plain", spec.DisplayTitle);
    }

    [Fact]
    public void Relabeler_Construction_ReadsNoLanguage()
    {
        var loc = new CountingLocalization();
        _ = new FrameworkHotkeyRelabeler(FrameworkHotkeys(), loc);
        Assert.Equal(0, loc.Calls);
    }

    [Fact]
    public void Relabeler_FirstRender_RelabelsOnce_ThenLanguageChange_RelabelsAgain()
    {
        var loc = new CountingLocalization();
        var hotkeys = FrameworkHotkeys();
        var relabeler = new FrameworkHotkeyRelabeler(hotkeys, loc);
        var n = FrameworkHotkeyLabels.DescriptionKeys.Count;

        relabeler.EnsureRelabeled();
        Assert.Equal(n, loc.Calls);
        foreach (var a in ((IHotkeyDirectory)hotkeys).Actions)
            Assert.StartsWith("ko:", a.Description);

        relabeler.EnsureRelabeled();   // every later row render: no further reads
        relabeler.EnsureRelabeled();
        Assert.Equal(n, loc.Calls);

        loc.RaiseChanged();
        Assert.Equal(2 * n, loc.Calls);
    }

    private sealed class NullInput : IInputGateway
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
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
        public void Debug(string message) { }
    }
}
