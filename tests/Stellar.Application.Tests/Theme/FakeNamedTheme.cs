// tests/Stellar.Application.Tests/Theme/FakeNamedTheme.cs
using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Tests.Theme;

internal sealed class FakeNamedTheme : INamedTheme
{
    public FakeNamedTheme(ThemePreset p) => Active = p;
    public ThemePreset Active { get; private set; }
    public string? ActiveCustomName { get; private set; }
    public float FontScale => 1f;
    public event Action? ActiveChanged;
    public void SetActive(ThemePreset preset) { Active = preset; ActiveCustomName = null; ActiveChanged?.Invoke(); }
    public void SetActiveCustom(string name, ThemePreset basePreset) { ActiveCustomName = name; Active = basePreset; ActiveChanged?.Invoke(); }
    public void SetFontScale(float scale) { }
    public void NotifyColorsChanged() => ActiveChanged?.Invoke();
}
