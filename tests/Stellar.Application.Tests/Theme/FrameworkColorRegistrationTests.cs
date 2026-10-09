// tests/Stellar.Application.Tests/Theme/FrameworkColorRegistrationTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.Theme;
using Xunit;

namespace Stellar.Application.Tests.Theme;

// Pins two things a silent mismatch between FrameworkColorRegistration's two internal dictionaries would
// break: (1) every editable slot has a label-key to relabel into the active language (a slot key present in
// EditableTokens but missing from LabelKeys would register fine but never pick up the real language — the
// owner review of 15ee53a, Important #1/#3); (2) RelabelAll actually resolves and writes every one of them,
// without touching anything else about the slot.
public sealed class FrameworkColorRegistrationTests
{
    private sealed class FakeLocalization : ILocalization
    {
        private readonly Dictionary<string, string> _map;
        public FakeLocalization(Dictionary<string, string> map) => _map = map;
        public string Language => "ko";
        public event Action? LanguageChanged { add { } remove { } }
        public string T(string key) => _map.TryGetValue(key, out var v) ? v : key;
        public string TFormat(string key, params object[] args) => string.Format(T(key), args);
    }

    private static ColorRegistryService NewRegistry()
    {
        var svc = new ColorRegistryService(new FakeNamedTheme(ThemePreset.Default), new NullOverrideStore());
        FrameworkColorRegistration.RegisterAll(svc);
        return svc;
    }

    [Fact]
    public void LabelKeys_CoversExactlyEditableTokens()
    {
        var editableTokenKeys = new HashSet<string>(FrameworkColorRegistration.EditableTokens.Keys);
        var labelKeySlotKeys = new HashSet<string>(FrameworkColorRegistration.LabelKeys.Keys);
        Assert.Equal(editableTokenKeys, labelKeySlotKeys);
    }

    [Fact]
    public void RelabelAll_RelabelsEverySlotInEditableTokens_ToItsRealLabel()
    {
        var svc = NewRegistry();
        var loc = new FakeLocalization(new Dictionary<string, string>
        {
            ["theme.color.accent"] = "강조색",
            ["theme.color.menuBackground"] = "패널 배경",
            ["theme.color.menuAccent"] = "패널 강조색",
            ["theme.color.menuBorder"] = "패널 테두리",
            ["theme.color.warning"] = "경고",
            ["theme.color.hudAccent"] = "HUD 강조색",
        });

        FrameworkColorRegistration.RelabelAll(svc, loc);

        var byKey = svc.Slots.ToDictionary(s => s.Key, s => s.Label);
        Assert.Equal("강조색", byKey["Theme.Accent"]);
        Assert.Equal("패널 배경", byKey["Theme.MenuBackground"]);
        Assert.Equal("패널 강조색", byKey["Theme.MenuAccent"]);
        Assert.Equal("패널 테두리", byKey["Theme.MenuBorder"]);
        Assert.Equal("경고", byKey["Theme.Warning"]);
        Assert.Equal("HUD 강조색", byKey["Theme.HudAccent"]);
    }

    [Fact]
    public void RelabelAll_NeverChangesKeyOwnerOrColourResolution()
    {
        var svc = NewRegistry();
        var beforeColour = svc.Resolve("Theme.Accent");
        var loc = new FakeLocalization(new Dictionary<string, string> { ["theme.color.accent"] = "강조색" });

        FrameworkColorRegistration.RelabelAll(svc, loc);

        Assert.Equal(beforeColour, svc.Resolve("Theme.Accent"));      // colour (preset defaults) untouched
        var slot = svc.Slots.Single(s => s.Key == "Theme.Accent");
        Assert.Equal("Theme", slot.Owner);                            // owner (identity) untouched
        Assert.Equal("강조색", slot.Label);
    }
}
