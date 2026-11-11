using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>Layout-editor "Hide others" on mod windows: <c>WindowService.SyncEditHidden</c> is a transient draw-suppress
/// that never changes the user's Visible flag, only applies to edit-mode windows, and re-evaluates per call.</summary>
public sealed class WindowEditHideTests
{
    private sealed class Renderer : IWindowRenderer
    {
        public readonly Dictionary<string, bool> Hide = new();
        public bool IsCanvasAvailable() => true;
        public object? Mount(WindowRegistration reg) => reg.Spec.Id;
        public bool IsAlive(object? token) => token != null;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) => Hide[reg.Spec.Id] = hide;
        public void SetRect(object? token, WindowRect rect) { }
        public WindowRect GetRect(object? token) => new(0, 0, 100, 50);
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) { }
    }

    private static WindowRegistration Reg(string id, bool editable = true) =>
        new(new WindowSpec(id, id, new WindowRect(0, 0, 100, 50), WindowCategory.HUD, WindowPanelStyle.Borderless)
            { ShouldRender = () => true, EditModeDragOnly = editable }, new TextElement(() => "x"));

    private static (WindowService svc, Renderer r) New()
    {
        var r = new Renderer();
        var svc = new WindowService(r, new Stellar.Application.Tests.NativeUi.NativeUiNullLog());
        svc.Register(Reg("a"));
        svc.Register(Reg("b"));
        svc.Register(Reg("menu", editable: false));
        svc.Tick(0.2f);
        return (svc, r);
    }

    private static bool Visible(WindowService svc, string id)
    {
        foreach (var e in svc.EditableElements()) if (e.Id == id) return e.Visible;
        throw new KeyNotFoundException(id);
    }

    [Fact] // Hide → draw-suppressed, Visible untouched; clear → drawn again.
    public void SyncEditHidden_SuppressesDraw_WithoutTouchingVisible()
    {
        var (svc, r) = New();
        Assert.Equal(2, svc.SyncEditHidden(_ => true));
        svc.Tick(0.016f);
        Assert.True(r.Hide["a"]);
        Assert.True(Visible(svc, "a"));
        Assert.False(r.Hide["menu"]);                       // non-edit-mode windows (menus, toolbar) are never hidden

        Assert.Equal(0, svc.SyncEditHidden(null));
        svc.Tick(0.016f);
        Assert.False(r.Hide["a"]);
        Assert.False(svc.IsEditHidden("a"));
    }

    [Fact] // A changed predicate (filter change) re-evaluates at once.
    public void PredicateChange_ReEvaluates()
    {
        var (svc, r) = New();
        svc.SyncEditHidden(id => id == "a");
        svc.Tick(0.016f);
        Assert.True(r.Hide["a"]); Assert.False(r.Hide["b"]);
        svc.SyncEditHidden(id => id == "b");
        svc.Tick(0.016f);
        Assert.False(r.Hide["a"]); Assert.True(r.Hide["b"]);
    }

    [Fact] // A window the user had hidden stays hidden after the transient hide is cleared (and isn't counted).
    public void UserHiddenWindow_StaysHidden_AfterClear()
    {
        var (svc, _) = New();
        svc.SetVisiblePersist("a", false);
        Assert.Equal(1, svc.SyncEditHidden(_ => true));      // only "b" was visible
        svc.SyncEditHidden(null);
        Assert.False(Visible(svc, "a"));
        Assert.True(Visible(svc, "b"));
    }
}
