using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class LoadoutServiceTests
{
    private sealed class FakeProbe : ILoadoutProbe
    {
        public bool Resolved = true;
        public List<LoadoutEntry> Entries = new();
        public int? Current;
        public int AppliedIndex = -1;
        public LoadoutResult ApplyReturns = LoadoutResult.Success;

        public bool IsResolved => Resolved;
        public IReadOnlyList<LoadoutEntry> ReadLoadouts() => Entries;
        public int? ReadCurrentIndex() => Current;
        public Task<LoadoutResult> CallApplyAsync(int index, CancellationToken ct)
        {
            AppliedIndex = index;
            return Task.FromResult(ApplyReturns);
        }
    }

    [Fact]
    public void Tick_marks_current_slot_and_raises_changed_once()
    {
        var probe = new FakeProbe
        {
            Entries = new() { new(0, "Ici-LF"), new(1, "Tank") },
            Current = 1,
        };
        var svc = new LoadoutService(probe);
        var raised = 0;
        svc.LoadoutsChanged += () => raised++;

        svc.Tick();
        svc.Tick();   // unchanged -> no second raise

        Assert.Equal(1, raised);
        var slots = svc.GetSlots();
        Assert.Equal(2, slots.Count);
        Assert.False(slots[0].IsCurrent);
        Assert.True(slots[1].IsCurrent);
        Assert.Equal(1, svc.CurrentIndex);
    }

    [Fact]
    public void Tick_raises_again_when_selection_changes()
    {
        var probe = new FakeProbe { Entries = new() { new(0, "A"), new(1, "B") }, Current = 0 };
        var svc = new LoadoutService(probe);
        var raised = 0;
        svc.LoadoutsChanged += () => raised++;

        svc.Tick();
        probe.Current = 1;
        svc.Tick();

        Assert.Equal(2, raised);
        Assert.Equal(1, svc.CurrentIndex);
    }

    [Fact]
    public async Task ApplyAsync_passes_index_to_probe_and_returns_result()
    {
        var probe = new FakeProbe { ApplyReturns = LoadoutResult.InCombat };
        var svc = new LoadoutService(probe);

        var result = await svc.ApplyAsync(3);

        Assert.Equal(3, probe.AppliedIndex);
        Assert.Equal(LoadoutResult.InCombat, result);
    }

    [Fact]
    public void IsAvailable_reflects_probe_resolution()
    {
        var probe = new FakeProbe { Resolved = false };
        var svc = new LoadoutService(probe);
        Assert.False(svc.IsAvailable);
    }
}
