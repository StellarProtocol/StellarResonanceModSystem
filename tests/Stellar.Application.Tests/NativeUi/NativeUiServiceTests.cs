using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.NativeUi;

public sealed class NativeUiServiceTests
{
    private static readonly Resolution Res = new(1920, 1080);

    private sealed class SlotBox { public int Value; }

    private static (NativeUiService svc, FakeAdapter adapter, InMemoryConfig cfg, SlotBox slot) New()
    {
        var adapter = new FakeAdapter();
        var cfg = new InMemoryConfig();
        var slot = new SlotBox();
        var targets = new List<NativeUiEntryDescriptor>
        {
            new("gameui.test", "Test", "zuiroot/x/y"),
        };
        return (new NativeUiService(adapter, cfg, () => slot.Value, new NullLog(), targets), adapter, cfg, slot);
    }

    [Fact] // The regression: resolving an element the user never touched must not move it.
    public void UnmodifiedEntry_IsNeverWritten_OnResolveOrReassert()
    {
        var (svc, adapter, _, _) = New();

        svc.Tick(5f, Res); // crosses both the 5s resolve and 1s reassert thresholds
        svc.Tick(1f, Res); // another reassert pass

        Assert.Equal(1, adapter.ResolveCount);
        Assert.Equal(0, adapter.SetRectCount);
        Assert.Equal(0, adapter.SetVisibleCount);
    }

    [Fact]
    public void AfterUserMove_ReassertReappliesTheRect()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(5f, Res); // resolve

        svc.SetRect("gameui.test", new WindowRect(100, 200, 50, 40));
        var afterMove = adapter.SetRectCount; // 1 (the user write)

        svc.Tick(1f, Res); // reassert pass

        Assert.True(adapter.SetRectCount > afterMove, "modified entry should be re-asserted");
    }

    [Fact]
    public void SavedOverride_IsAppliedOnResolve_AndReasserted()
    {
        var (svc, adapter, cfg, _) = New();
        // Persisted override for this (slot × id × resolution).
        const string prefix = "slot0.gameui.test.1920x1080";
        cfg.Set($"{prefix}.x", 300f);
        cfg.Set($"{prefix}.y", 150f);
        cfg.Set($"{prefix}.w", 200f);
        cfg.Set($"{prefix}.h", 80f);
        cfg.Set($"{prefix}.visible", true);

        svc.Tick(5f, Res); // resolve loads + applies the override
        var afterResolve = adapter.SetRectCount;
        svc.Tick(1f, Res); // and keeps defending it

        Assert.True(afterResolve >= 1, "saved override should apply on resolve");
        Assert.True(adapter.SetRectCount > afterResolve, "saved override should be re-asserted");
    }

    [Fact]
    public void Reset_FullyRestores_AndStopsReasserting()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(5f, Res);
        svc.SetRect("gameui.test", new WindowRect(100, 200, 50, 40));

        svc.ResetToOriginal("gameui.test");
        var setRectAtReset = adapter.SetRectCount;

        Assert.Equal(1, adapter.RestoreCount);     // full original-pose writeback, not a SetRect round-trip
        svc.Tick(1f, Res);                          // reassert pass
        Assert.Equal(setRectAtReset, adapter.SetRectCount); // no longer modified => not re-asserted
    }

    [Fact] // Scene change destroys + rebuilds the game HUD: a stale (no-longer-alive) entry must re-resolve and
           // re-apply its saved override to the new element (else the move is lost on scene change — bug #4).
    public void SceneChange_ReResolves_AndReappliesSavedOverride()
    {
        var (svc, adapter, cfg, _) = New();
        const string prefix = "slot0.gameui.test.1920x1080";
        cfg.Set($"{prefix}.x", 300f); cfg.Set($"{prefix}.y", 150f);
        cfg.Set($"{prefix}.w", 200f); cfg.Set($"{prefix}.h", 80f); cfg.Set($"{prefix}.visible", true);

        svc.Tick(5f, Res);                          // resolve #1 + apply override
        Assert.Equal(1, adapter.ResolveCount);
        var setRectAtResolve1 = adapter.SetRectCount;

        adapter.Alive = false;                      // simulate the scene-change destroy
        svc.Tick(5f, Res);                          // resolve pass detects the dead handle
        Assert.Equal(2, adapter.ResolveCount);      // re-resolved the rebuilt element
        Assert.True(adapter.SetRectCount > setRectAtResolve1, "saved override re-applied to the new element");
    }

    [Fact] // Editor enumeration includes a hidden entry, and reports SafeToHide as CanHide.
    public void EditableElements_IncludesHidden_AndReportsCanHide()
    {
        var (svc, _, _, _) = New();
        svc.Tick(5f, Res);                                  // resolve gameui.test (SafeToHide=true default)

        svc.SetVisible("gameui.test", false);               // hide it
        var els = new List<EditableElement>(svc.EditableElements());

        var e = Assert.Single(els);
        Assert.Equal("gameui.test", e.Id);
        Assert.False(e.Visible);                            // still enumerated while hidden
        Assert.True(e.CanHide);
    }

    [Fact] // Drag-move must not disk-write every frame; persistence waits for Commit (release).
    public void SetRect_DoesNotPersist_UntilCommit()
    {
        var (svc, _, cfg, _) = New();
        svc.Tick(5f, Res);

        svc.SetRect("gameui.test", new WindowRect(100, 100, 40, 40)); // mid-drag frame
        Assert.DoesNotContain("slot0.gameui.test.1920x1080.x", cfg.Keys);

        svc.Commit("gameui.test");                              // release
        Assert.Contains("slot0.gameui.test.1920x1080.x", cfg.Keys);
    }

    [Fact] // A layout slot captures native-UI positions independently per slot.
    public void Slots_StoreNativePositionsIndependently()
    {
        var (svc, adapter, cfg, slot) = New();
        svc.Tick(5f, Res);                                  // resolve on slot 0

        svc.SetRect("gameui.test", new WindowRect(100, 100, 40, 40));
        svc.Commit("gameui.test");                          // release => persist under slot0
        Assert.Contains("slot0.gameui.test.1920x1080.x", cfg.Keys);

        slot.Value = 1;                                     // user switches to slot 1
        svc.ReapplyForActiveSlot(Res);
        var restoreAfterSwitch = adapter.RestoreCount;
        Assert.True(restoreAfterSwitch >= 1, "switching to a slot with no override snaps back to original");

        svc.SetRect("gameui.test", new WindowRect(500, 500, 40, 40));
        svc.Commit("gameui.test");                          // persist under slot1
        Assert.Contains("slot1.gameui.test.1920x1080.x", cfg.Keys);
        // slot0's value is untouched.
        Assert.Equal(100f, cfg.Get("slot0.gameui.test.1920x1080.x", -1f));
        Assert.Equal(500f, cfg.Get("slot1.gameui.test.1920x1080.x", -1f));
    }

    private sealed class FakeAdapter : INativeUiAdapter
    {
        public int ResolveCount, SetRectCount, SetVisibleCount, RestoreCount;
        public bool Alive = true;   // toggled by scene-change self-heal tests; resolved entries stay resolved while true
        public bool TryResolve(string allowlistPath, string? rectChildPath, out NativeUiHandle handle)
        {
            ResolveCount++;
            handle = new NativeUiHandle
            {
                AllowlistPath = allowlistPath,
                GameObjectRef = (IntPtr)1,
                OriginalRect  = new WindowRect(10, 20, 100, 40),
            };
            return true;
        }
        public void SetVisible(NativeUiHandle handle, bool visible) => SetVisibleCount++;
        public void SetRect(NativeUiHandle handle, WindowRect rect) => SetRectCount++;
        public void RestoreOriginal(NativeUiHandle handle) => RestoreCount++;
        public bool IsAlive(NativeUiHandle handle) => Alive;
        public WindowRect GetCurrentRect(NativeUiHandle handle) => handle.OriginalRect;
        public void DumpDiagnostics(System.Action<string> log) { }
    }

    private sealed class InMemoryConfig : IConfigSection
    {
        private readonly Dictionary<string, object?> _v = new();
        public System.Collections.Generic.IEnumerable<string> Keys => _v.Keys;
        public T? Get<T>(string key, T? def) => _v.TryGetValue(key, out var x) && x is T t ? t : def;
        public void Set<T>(string key, T value) => _v[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
    }

    private sealed class NullLog : IPluginLog
    {
        public void Info(string m) { }
        public void Warning(string m) { }
        public void Error(string m) { }
        public void Debug(string m) { }
    }
}
