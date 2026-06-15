// tests/Stellar.Application.Tests/Services/LayoutStorageTests.cs
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public sealed class LayoutStorageTests
{
    [Fact]
    public void Get_NoSavedLayout_ReturnsDefault()
    {
        var storage = MakeStorage(out _);
        var def = new WindowRect(20, 20, 320, 150);

        var result = storage.Get(slot: 0, windowId: "playerhud.main",
                                 resolution: new Resolution(1920, 1080),
                                 defaultRect: def);

        Assert.Equal(def, result.Rect);
        Assert.True(result.Visible);
    }

    [Fact]
    public void Save_ThenGet_RoundTrips()
    {
        var storage = MakeStorage(out _);
        var rect = new WindowRect(100, 200, 400, 300);
        var res = new Resolution(1920, 1080);

        storage.Save(slot: 0, windowId: "p.main", resolution: res, rect: rect, visible: false);
        var result = storage.Get(0, "p.main", res, new WindowRect(0, 0, 0, 0));

        Assert.Equal(rect, result.Rect);
        Assert.False(result.Visible);
    }

    [Fact]
    public void Get_DifferentResolutionWithinDelta_UsesClosestMatch()
    {
        var storage = MakeStorage(out _);
        storage.Save(0, "p.main", new Resolution(1920, 1080), new WindowRect(10, 20, 30, 40), true);

        // 1900x1080 is within 10% delta of 1920x1080.
        var result = storage.Get(0, "p.main", new Resolution(1900, 1080), new WindowRect(99, 99, 99, 99));

        Assert.Equal(new WindowRect(10, 20, 30, 40), result.Rect);
    }

    [Fact]
    public void Get_DifferentResolutionBeyondDelta_FallsBackToDefault()
    {
        var storage = MakeStorage(out _);
        storage.Save(0, "p.main", new Resolution(1920, 1080), new WindowRect(10, 20, 30, 40), true);

        // 1280x720 is more than 10% from 1920x1080.
        var def = new WindowRect(99, 99, 99, 99);
        var result = storage.Get(0, "p.main", new Resolution(1280, 720), def);

        Assert.Equal(def, result.Rect);
    }

    [Fact]
    public void Get_DifferentSlot_ReturnsDefault()
    {
        var storage = MakeStorage(out _);
        storage.Save(0, "p.main", new Resolution(1920, 1080), new WindowRect(1, 2, 3, 4), true);

        var def = new WindowRect(99, 99, 99, 99);
        var result = storage.Get(slot: 1, "p.main", new Resolution(1920, 1080), def);

        Assert.Equal(def, result.Rect);
    }

    [Fact]
    public void Slots_AreFourInitially()
    {
        var storage = MakeStorage(out _);
        Assert.Equal(4, storage.SlotCount);
    }

    [Fact]
    public void GetSlotName_DefaultsAreLabeled()
    {
        var storage = MakeStorage(out _);
        Assert.Equal("Default", storage.GetSlotName(0));
        Assert.Equal("Slot 2",  storage.GetSlotName(1));
        Assert.Equal("Slot 3",  storage.GetSlotName(2));
        Assert.Equal("Slot 4",  storage.GetSlotName(3));
    }

    [Fact]
    public void SetActiveSlot_Persists()
    {
        var storage = MakeStorage(out var config);
        storage.SetActiveSlot(2);

        var reread = new LayoutStorage(config, new NullLog());
        Assert.Equal(2, reread.ActiveSlot);
    }

    [Fact]
    public void ActiveSlot_DefaultsToZero()
    {
        var storage = MakeStorage(out _);
        Assert.Equal(0, storage.ActiveSlot);
    }

    [Fact]
    public void Save_PersistsAcrossInstance()
    {
        var storage1 = MakeStorage(out var config);
        storage1.Save(0, "p.main", new Resolution(1920, 1080), new WindowRect(50, 60, 70, 80), true);

        var storage2 = new LayoutStorage(config, new NullLog());
        var result = storage2.Get(0, "p.main", new Resolution(1920, 1080), new WindowRect(0, 0, 0, 0));

        Assert.Equal(new WindowRect(50, 60, 70, 80), result.Rect);
    }

    [Fact]
    public void ResetSlot_ClearsAllWindows()
    {
        var storage = MakeStorage(out _);
        storage.Save(0, "a.main", new Resolution(1920, 1080), new WindowRect(1, 2, 3, 4), true);
        storage.Save(0, "b.main", new Resolution(1920, 1080), new WindowRect(5, 6, 7, 8), true);

        storage.ResetSlot(0);

        var resA = storage.Get(0, "a.main", new Resolution(1920, 1080), new WindowRect(99, 99, 99, 99));
        Assert.Equal(new WindowRect(99, 99, 99, 99), resA.Rect);
    }

    [Fact]
    public void ResetSlot_DoesNotAffectOtherSlots()
    {
        var storage = MakeStorage(out _);
        storage.Save(0, "p.main", new Resolution(1920, 1080), new WindowRect(1, 2, 3, 4), true);
        storage.Save(1, "p.main", new Resolution(1920, 1080), new WindowRect(5, 6, 7, 8), true);

        storage.ResetSlot(0);

        var res1 = storage.Get(1, "p.main", new Resolution(1920, 1080), new WindowRect(99, 99, 99, 99));
        Assert.Equal(new WindowRect(5, 6, 7, 8), res1.Rect);
    }

    [Fact]
    public void Save_InvalidSlotIndex_LoggedAndNoOp()
    {
        var log = new RecordingLog();
        var storage = MakeStorage(out _, log);

        storage.Save(slot: 99, "p.main", new Resolution(1920, 1080), new WindowRect(0, 0, 1, 1), true);

        Assert.Single(log.Warnings, w => w.Contains("slot 99"));
    }

    [Fact]
    public void Get_HidesWindow_VisibleFalseRoundTrips()
    {
        var storage = MakeStorage(out _);
        storage.Save(0, "p.main", new Resolution(1920, 1080), new WindowRect(1, 2, 3, 4), visible: false);

        var result = storage.Get(0, "p.main", new Resolution(1920, 1080), new WindowRect(0, 0, 0, 0));

        Assert.False(result.Visible);
    }

    // Test doubles
    private static LayoutStorage MakeStorage(out InMemoryConfig config, IPluginLog? log = null)
    {
        config = new InMemoryConfig();
        return new LayoutStorage(config, log ?? new NullLog());
    }

    private sealed class InMemoryConfig : IPluginConfig
    {
        private readonly Dictionary<string, InMemorySection> _sections = new();
#pragma warning disable CS0067   // unused event in test stub
        public event System.Action<string>? SectionChanged;
#pragma warning restore CS0067
        public IConfigSection GetSection(string name)
        {
            if (!_sections.TryGetValue(name, out var s))
            {
                s = new InMemorySection();
                _sections[name] = s;
            }
            return s;
        }
    }

    private sealed class InMemorySection : IConfigSection
    {
        private readonly Dictionary<string, object?> _store = new();
        public T? Get<T>(string key, T? defaultValue)
            => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { /* no-op for tests */ }
        public void SaveQuiet() { /* no-op for tests */ }
    }

    private sealed class NullLog : IPluginLog
    {
        public void Info(string m)    { }
        public void Warning(string m) { }
        public void Error(string m)   { }
        public void Debug(string m)   { }
    }

    private sealed class RecordingLog : IPluginLog
    {
        public List<string> Infos    { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Errors   { get; } = new();
        public List<string> Debugs   { get; } = new();
        public void Info(string m)    => Infos.Add(m);
        public void Warning(string m) => Warnings.Add(m);
        public void Error(string m)   => Errors.Add(m);
        public void Debug(string m)   => Debugs.Add(m);
    }
}
