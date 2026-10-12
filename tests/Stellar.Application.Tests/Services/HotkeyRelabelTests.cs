using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Application.Tests.Theme;
using Stellar.Infrastructure.Localization;
using Stellar.Infrastructure.UI.SettingsPanels;
using Xunit;

namespace Stellar.Application.Tests.Services;

// Pins the framework-hotkey relabel: the framework declares its own actions with an English description
// (before the client language is safely readable) and the Hotkeys panel relabels them render-driven. Relabel
// must touch ONLY the description, and the id → catalog-key map must cover exactly the framework's own ids —
// an id missing from the map would silently stay English.
public sealed class HotkeyRelabelTests
{
    [Fact]
    public void Relabel_ChangesOnlyTheDescription()
    {
        var config = new InMemoryConfigSection();
        var svc = new HotkeyService(new FakeInputGateway(), new NullLog(), config);
        var handle = svc.DeclareAction(
            new HotkeyAction("framework.x", "English", new KeyBinding(StellarKeyCode.F1, ModifierKeys.Alt)),
            callback: () => { });
        svc.Rebind("framework.x", new KeyBinding(StellarKeyCode.F2));
        var saves = config.SaveCallCount;
        var changed = 0;
        ((IHotkeyDirectory)svc).BindingChanged += _ => changed++;

        svc.Relabel("framework.x", "한국어");

        var dir = (IHotkeyDirectory)svc;
        var action = Assert.Single(dir.Actions);
        Assert.Same(handle, action);
        Assert.Equal("한국어", action.Description);
        Assert.Equal("framework.x", action.Id);
        Assert.Equal(new KeyBinding(StellarKeyCode.F2), action.CurrentBinding);
        Assert.Equal(new KeyBinding(StellarKeyCode.F1, ModifierKeys.Alt), dir.GetSuggestedDefault("framework.x"));
        Assert.Equal(saves, config.SaveCallCount);   // nothing persisted
        Assert.Equal(0, changed);                    // not a binding change
    }

    [Fact]
    public void Relabel_UnknownId_IsNoOp()
    {
        var svc = new HotkeyService(new FakeInputGateway(), new NullLog());
        svc.DeclareAction(new HotkeyAction("framework.x", "English", null), callback: () => { });

        svc.Relabel("framework.nope", "X");

        var action = Assert.Single(((IHotkeyDirectory)svc).Actions);
        Assert.Equal("English", action.Description);
    }

    [Fact]
    public void DescriptionKeys_CoverExactlyTheFrameworkHotkeyIds()
    {
        var ids = typeof(FrameworkHotkeyIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        Assert.NotEmpty(ids);
        Assert.True(ids.SetEquals(FrameworkHotkeyLabels.DescriptionKeys.Keys),
            $"ids [{string.Join(", ", ids.OrderBy(x => x))}] vs map [{string.Join(", ", FrameworkHotkeyLabels.DescriptionKeys.Keys.OrderBy(x => x))}]");
    }

    [Fact]
    public void FrameworkHotkeyIds_AreTheShippedPersistedIds()
    {
        // Persisted as config keys (hotkeys.bindings.<id>.*) — a changed value orphans every saved binding.
        Assert.Equal("framework.layout-edit", FrameworkHotkeyIds.LayoutEdit);
        Assert.Equal("framework.hud-toggle", FrameworkHotkeyIds.HudToggle);
        Assert.Equal("framework.hud-hold", FrameworkHotkeyIds.HudHold);
        Assert.Equal("framework.perf-toggle", FrameworkHotkeyIds.PerfToggle);
        Assert.Equal("framework.settings-toggle", FrameworkHotkeyIds.SettingsToggle);
    }

    [Fact]
    public void DescriptionKeys_ExistInEveryFrameworkCatalog()
    {
        var catalogs = FrameworkCatalogs.Read().ToList();
        Assert.Contains(catalogs, c => c.code == "en");
        foreach (var (code, json) in catalogs)
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var key in FrameworkHotkeyLabels.DescriptionKeys.Values)
                Assert.True(doc.RootElement.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 },
                    $"{code}.json lacks '{key}'");
        }
    }

    [Fact]
    public void RelabelAll_WritesEveryDeclaredFrameworkAction()
    {
        var svc = new HotkeyService(new FakeInputGateway(), new NullLog());
        foreach (var id in FrameworkHotkeyLabels.DescriptionKeys.Keys)
            svc.DeclareAction(new HotkeyAction(id, "English", null), callback: () => { });
        svc.DeclareAction(new HotkeyAction("combatmeter.toggle", "Plugin text", null), callback: () => { });

        FrameworkHotkeyLabels.RelabelAll(svc, new FakeLocalization());

        foreach (var a in ((IHotkeyDirectory)svc).Actions)
        {
            if (FrameworkHotkeyLabels.DescriptionKeys.TryGetValue(a.Id, out var key))
                Assert.Equal("ko:" + key, a.Description);
            else
                Assert.Equal("Plugin text", a.Description);   // plugin actions are never touched
        }
    }

    private sealed class FakeLocalization : ILocalization
    {
        public string Language => "ko";
        public event Action? LanguageChanged { add { } remove { } }
        public string T(string key) => "ko:" + key;
        public string TFormat(string key, params object[] args) => T(key);
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
