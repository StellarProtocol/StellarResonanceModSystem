using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests.Localization;

internal sealed class FakeProbe : IClientLanguageProbe
{
    public string SupportedLanguage { get; set; } = "en";
    public event System.Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();
}

/// <summary>Scripted raw game read: <see cref="Index"/> is what the game's static currently holds.</summary>
internal sealed class FakeLanguageReader : IClientLanguageReader
{
    public int Index { get; set; } = -1;
    public int Reads { get; private set; }
    public int ReadLanguageIndex() { Reads++; return Index; }
}

internal sealed class FakeLog : IPluginLog
{
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
}

internal sealed class FakeConfigSection : IConfigSection
{
    private readonly Dictionary<string, object?> _v = new();
    public int SaveCount { get; private set; }

    public T? Get<T>(string key, T? defaultValue)
        => _v.TryGetValue(key, out var o) && o is T t ? t : defaultValue;

    public void Set<T>(string key, T value) => _v[key] = value;
    public void Save() => SaveCount++;
    public void SaveQuiet() { }
    public void RemoveByPrefix(string prefix) { }
}
