using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>Records wrapper calls and fakes a measurement that follows the applied offset (base rect (10,20,100,40)
/// at 2 px per local unit), so the service's screen ⇄ offset math round-trips like the real adapter.</summary>
internal sealed class FakeNativeUiAdapter : INativeUiAdapter
{
    public static readonly WindowRect BaseRect = new(10, 20, 100, 40);
    public const float Px = 2f;

    public int ResolveCount, ApplyCount, UnwrapCount, MeasureCount;
    public bool Alive = true, WrapIntact = true, Showing = true;
    public readonly HashSet<string> Absent = new();                                  // descriptor ids that don't resolve
    public readonly Dictionary<string, (float X, float Y, bool Visible)> Applied = new();   // by descriptor id
    private readonly Dictionary<string, string> _idByPath = new();

    public bool TryResolve(NativeUiEntryDescriptor entry, out NativeUiHandle handle)
    {
        handle = default;
        if (Absent.Contains(entry.Id)) return false;
        ResolveCount++;
        Alive = true;   // the (re)built element exists
        _idByPath[entry.Path] = entry.Id;
        handle = new NativeUiHandle { AllowlistPath = entry.Path, GameObjectRef = (System.IntPtr)1 };
        return true;
    }

    public bool IsAlive(NativeUiHandle handle) => Alive;
    public bool IsWrapIntact(NativeUiHandle handle) => WrapIntact;

    public void Apply(NativeUiHandle handle, float offsetX, float offsetY, bool visible)
    {
        ApplyCount++;
        WrapIntact = true;
        Applied[_idByPath[handle.AllowlistPath]] = (offsetX, offsetY, visible);
    }

    public void Unwrap(NativeUiHandle handle)
    {
        UnwrapCount++;
        Applied.Remove(_idByPath[handle.AllowlistPath]);
    }

    public NativeUiMeasure Measure(NativeUiHandle handle)
    {
        MeasureCount++;
        var (ox, oy) = Applied.TryGetValue(_idByPath[handle.AllowlistPath], out var a) ? (a.X, a.Y) : (0f, 0f);
        var r = new WindowRect(BaseRect.X + ox * Px, BaseRect.Y - oy * Px, BaseRect.Width, BaseRect.Height);
        return new NativeUiMeasure(r, Showing, Px);
    }

    public void DumpDiagnostics(System.Action<string> log) { }
}

internal sealed class NativeUiMemoryConfig : IConfigSection, IConfigKeyReader
{
    private readonly Dictionary<string, object?> _v = new();
    public int Saves, QuietSaves;
    public IEnumerable<string> Keys => _v.Keys;
    public T? Get<T>(string key, T? def) => _v.TryGetValue(key, out var x) && x is T t ? t : def;
    public void Set<T>(string key, T value) => _v[key] = value;
    public void Save() => Saves++;
    public void SaveQuiet() => QuietSaves++;
    public void RemoveByPrefix(string prefix)
    {
        foreach (var k in new List<string>(_v.Keys))
            if (k.StartsWith(prefix, System.StringComparison.Ordinal)) _v.Remove(k);
    }
    public IEnumerable<string> KeysWithPrefix(string prefix)
    {
        foreach (var k in new List<string>(_v.Keys))
            if (k.StartsWith(prefix, System.StringComparison.Ordinal)) yield return k;
    }
}

internal sealed class NativeUiNullLog : IPluginLog
{
    public void Info(string m) { }
    public void Warning(string m) { }
    public void Error(string m) { }
    public void Debug(string m) { }
}
