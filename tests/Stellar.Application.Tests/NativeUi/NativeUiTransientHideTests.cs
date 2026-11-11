using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;
using static Stellar.Application.Tests.NativeUi.NativeUiServiceTests;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>Layout-editor "Hide others" on the game UI: a transient wrapper scale-0 that never persists and never
/// touches Visible / IsModified / the offset, so undo restores exactly the prior state.</summary>
public sealed class NativeUiTransientHideTests
{
    private static NativeUiService.EntryState Entry(NativeUiService svc)
    {
        foreach (var e in svc.Entries) return e;
        throw new KeyNotFoundException();
    }

    [Fact] // An untouched element gets a temporary offset-0 wrapper at scale 0, removed again on undo; nothing saved.
    public void UntouchedElement_TempWrappedHidden_ThenUnwrapped_NothingPersisted()
    {
        var (svc, adapter, cfg, _) = New();
        svc.Tick(0.2f, Res);
        Assert.Equal(1, svc.SetTransientHidden(true));
        Assert.Equal((0f, 0f, false), adapter.Applied[Id]);
        var unwraps = adapter.UnwrapCount;

        Assert.Equal(0, svc.SetTransientHidden(false));
        Assert.True(adapter.UnwrapCount > unwraps);
        Assert.False(adapter.Applied.ContainsKey(Id));
        Assert.False(Entry(svc).IsModified);
        Assert.True(Entry(svc).Visible);
        Assert.Empty(new List<string>(cfg.KeysWithPrefix("slot")));
    }

    [Fact] // A moved element keeps its offset while temp-hidden and comes back shown at it.
    public void MovedElement_KeepsOffset_ComesBackShown()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetRect(Id, new WindowRect(110, 120, 100, 40));
        svc.Commit(Id);
        svc.SetTransientHidden(true);
        Assert.Equal((50f, -50f, false), adapter.Applied[Id]);
        svc.SetTransientHidden(false);
        Assert.Equal((50f, -50f, true), adapter.Applied[Id]);
    }

    [Fact] // Undo restores a USER hide: the element stays hidden, and the saved visibility was never touched.
    public void UserHiddenElement_StaysHiddenAfterUndo()
    {
        var (svc, adapter, cfg, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetVisible(Id, false);
        svc.SetTransientHidden(true);
        svc.SetTransientHidden(false);
        Assert.Equal((0f, 0f, false), adapter.Applied[Id]);
        Assert.False(cfg.Get($"{P}.visible", true));
        Assert.False(Entry(svc).Visible);
    }

    [Fact] // Hidden-by-filter elements are not editable: no outline / placeholder / selection.
    public void WhileTempHidden_NothingIsEditable()
    {
        var (svc, _, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetTransientHidden(true);
        Assert.Empty(new List<EditableElement>(svc.EditableElements()));
        Assert.False(svc.TryGetEditView(Entry(svc), out _));
        svc.SetTransientHidden(false);
        Assert.Single(new List<EditableElement>(svc.EditableElements()));
    }

    [Fact] // A scene rebuild while temp-hidden: the rebuilt element is re-wrapped hidden at once.
    public void Rebuild_WhileTempHidden_RewrapsHidden()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetTransientHidden(true);
        adapter.Alive = false;
        adapter.Applied.Clear();
        svc.Tick(0.1f, Res);
        Assert.Equal((0f, 0f, false), adapter.Applied[Id]);
    }

    [Fact] // A slot switch under the hide reloads the user's state underneath and keeps the element temp-hidden.
    public void SlotSwitch_WhileTempHidden_StaysHidden_UndoShowsNewSlotState()
    {
        var (svc, adapter, cfg, slot) = New();
        cfg.Set("slot1.gameui.test.1920x1080.ox", 7f); cfg.Set("slot1.gameui.test.1920x1080.oy", 3f);
        svc.Tick(0.2f, Res);
        svc.SetTransientHidden(true);
        slot.Value = 1;
        svc.ReapplyForActiveSlot(Res);
        Assert.Equal((7f, 3f, false), adapter.Applied[Id]);
        svc.SetTransientHidden(false);
        Assert.Equal((7f, 3f, true), adapter.Applied[Id]);
    }

    [Fact] // Re-applying the same state is a no-op (called every edit-mode tick).
    public void RepeatedSameState_DoesNotReapply()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetTransientHidden(true);
        var applies = adapter.ApplyCount;
        for (var i = 0; i < 10; i++) svc.SetTransientHidden(true);
        Assert.Equal(applies, adapter.ApplyCount);
    }
}
