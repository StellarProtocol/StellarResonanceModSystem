using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;
using static Stellar.Application.Tests.NativeUi.NativeUiServiceTests;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>Edit-mode placeholders: absent / hidden-by-game / hidden-by-user enumeration, placeholder geometry
/// (last-seen default rect + saved offset, else the allowlist default fraction) and editing a placeholder.</summary>
public sealed class NativeUiPlaceholderTests
{
    private const string BossId = "gameui.boss";
    private static readonly NativeUiEntryDescriptor Boss =
        new(BossId, "Boss", "zuiroot/boss") { DefaultFraction = new WindowRect(0.30f, 0.11f, 0.40f, 0.09f) };

    private static NativeUiService.EntryState Entry(NativeUiService svc, string id)
    {
        foreach (var e in svc.Entries) if (e.Descriptor.Id == id) return e;
        throw new KeyNotFoundException(id);
    }

    [Fact] // Before the HUD exists (title screen: nothing resolves) no placeholders are offered.
    public void NothingResolved_NoPlaceholders()
    {
        var (svc, adapter, _, _) = New(Boss);
        adapter.Absent.Add(Id);
        adapter.Absent.Add(BossId);
        svc.Tick(0.2f, Res);
        Assert.Empty(new List<EditableElement>(svc.EditableElements()));
    }

    [Fact] // An absent element (boss bar outside a fight) is a placeholder at its built-in default fraction rect.
    public void AbsentElement_IsPlaceholder_AtDefaultFraction()
    {
        var (svc, adapter, _, _) = New(Boss);
        adapter.Absent.Add(BossId);
        svc.Tick(0.2f, Res);

        Assert.True(svc.TryGetEditView(Entry(svc, BossId), out var v));
        Assert.Equal(NativeUiPresence.Absent, v.Presence);
        Assert.Equal(new WindowRect(576f, 118.8f, 768f, 97.2f), Round(v.Rect));
        Assert.Equal(2, new List<EditableElement>(svc.EditableElements()).Count);
    }

    [Fact] // Resolved but the game isn't drawing it → placeholder at the last-seen default rect + the saved offset.
    public void HiddenByGame_IsPlaceholder_AtLastSeenRectPlusOffset()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);
        Assert.True(svc.TryGetEditView(Entry(svc, Id), out var live));   // seen once while showing
        Assert.Equal(NativeUiPresence.Showing, live.Presence);
        svc.SetRect(Id, new WindowRect(110, 120, 100, 40));               // offset (50,-50) at 2 px/unit

        adapter.Showing = false;
        svc.Tick(0.2f, Res);                                              // let the 0.1 s measure cache expire
        Assert.True(svc.TryGetEditView(Entry(svc, Id), out var ph));
        Assert.Equal(NativeUiPresence.HiddenByGame, ph.Presence);
        Assert.Equal(new WindowRect(110, 120, 100, 40), ph.Rect);
    }

    [Fact] // Hidden by the user: still enumerated (dimmed re-enable outline), Visible=false, CanHide from SafeToHide.
    public void HiddenByUser_IsEnumerated_NotVisible()
    {
        var (svc, _, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetVisible(Id, false);
        var e = Assert.Single(svc.EditableElements());
        Assert.False(e.Visible);
        Assert.True(e.CanHide);
    }

    [Fact] // A placeholder is editable: moving/hiding it persists and applies the moment the element appears.
    public void EditingAbsentPlaceholder_AppliesWhenElementAppears()
    {
        var (svc, adapter, cfg, _) = New(Boss);
        adapter.Absent.Add(BossId);
        svc.Tick(0.2f, Res);
        var rect = svc.GetLiveRect(Entry(svc, BossId));
        svc.SetRect(BossId, new WindowRect(rect.X + 108, rect.Y, rect.Width, rect.Height));   // 1 px/unit at 1080p guess
        svc.Commit(BossId);
        Assert.Equal(108f, cfg.Get("slot0.gameui.boss.1920x1080.ox", 0f), 3);

        adapter.Absent.Clear();                                           // the boss fight starts
        svc.Tick(0.6f, Res);
        Assert.Equal(108f, adapter.Applied[BossId].X, 3);
    }

    [Fact] // Last-seen geometry is persisted per resolution (quietly), so placeholders survive a restart.
    public void SeenGeometry_IsPersisted()
    {
        var (svc, _, cfg, _) = New();
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);                                                // measure pass + flush
        Assert.Equal(10f, cfg.Get("seen.gameui.test.1920x1080.x", 0f));
        Assert.Equal(100f, cfg.Get("seen.gameui.test.1920x1080.w", 0f));
        Assert.Equal(2f, cfg.Get("seen.gameui.test.1920x1080.s", 0f));
        Assert.True(cfg.QuietSaves >= 1);
    }

    private static WindowRect Round(WindowRect r) => new(
        (float)System.Math.Round(r.X, 1), (float)System.Math.Round(r.Y, 1),
        (float)System.Math.Round(r.Width, 1), (float)System.Math.Round(r.Height, 1));
}
