using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Tests.Theme;

internal sealed class InMemoryConfigSection : IConfigSection
{
    public readonly Dictionary<string, object?> Values = new();
    public T? Get<T>(string key, T? defaultValue)
        => Values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
    public void Set<T>(string key, T value) => Values[key] = value;
    public void Save() { }
    public void SaveQuiet() { }
    public void RemoveByPrefix(string prefix)
    {
        foreach (var k in new List<string>(Values.Keys))
            if (k.StartsWith(prefix, System.StringComparison.Ordinal)) Values.Remove(k);
    }
}
