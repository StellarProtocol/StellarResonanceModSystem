// tests/Stellar.Application.Tests/Theme/MemoryOverrideStore.cs
using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;

namespace Stellar.Application.Tests.Theme;

internal sealed class MemoryOverrideStore : IColorOverrideStore
{
    private readonly Dictionary<(string,string), ColorRgba> _m = new();
    public bool TryGet(string t, string k, out ColorRgba v) => _m.TryGetValue((t,k), out v);
    public void Set(string t, string k, ColorRgba v) => _m[(t,k)] = v;
    public void Clear(string t, string k) => _m.Remove((t,k));
    public bool Has(string t, string k) => _m.ContainsKey((t,k));
    public int FlushCount { get; private set; }
    public void Flush() => FlushCount++;
    public bool RenameTheme(string oldName, string newName)
    {
        var changed = RemoveTheme(newName);
        foreach (var key in new List<(string,string)>(_m.Keys))
            if (key.Item1 == oldName) { _m[(newName, key.Item2)] = _m[key]; _m.Remove(key); changed = true; }
        return changed;
    }
    public bool RemoveTheme(string t)
    {
        var any = false;
        foreach (var key in new List<(string,string)>(_m.Keys)) if (key.Item1 == t) { _m.Remove(key); any = true; }
        return any;
    }
}
