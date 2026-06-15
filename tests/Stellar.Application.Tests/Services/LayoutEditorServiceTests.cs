// tests/Stellar.Application.Tests/Services/LayoutEditorServiceTests.cs
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public sealed class LayoutEditorServiceTests
{
    [Fact] public void IsEditing_DefaultsFalse() { Assert.False(NewSvc().IsEditing); }

    [Fact] public void Toggle_FlipsIsEditing()
    {
        var s = NewSvc(); s.ToggleEditMode();
        Assert.True(s.IsEditing);
        s.ToggleEditMode();
        Assert.False(s.IsEditing);
    }

    [Fact] public void SelectWindow_SetsSelectedId()
    {
        var s = NewSvc(); s.ToggleEditMode();
        s.SelectWindow("playerhud.main");
        Assert.Equal("playerhud.main", s.SelectedWindowId);
    }

    [Fact] public void SelectWindow_OutsideEditMode_NoEffect()
    {
        var s = NewSvc();
        s.SelectWindow("playerhud.main");
        Assert.Null(s.SelectedWindowId);
    }

    [Fact] public void BeginDrag_RecordsStartPosition()
    {
        var s = NewSvc(); s.ToggleEditMode(); s.SelectWindow("p.main");
        s.BeginDrag(startPointerX: 100, startPointerY: 200, startRect: new WindowRect(50, 60, 320, 150));
        Assert.True(s.IsDragging);
    }

    [Fact] public void UpdateDrag_ReturnsRectOffsetByDelta()
    {
        var s = NewSvc(); s.ToggleEditMode(); s.SelectWindow("p.main");
        s.BeginDrag(100, 200, new WindowRect(50, 60, 320, 150));
        var moved = s.UpdateDrag(pointerX: 130, pointerY: 230, otherWindows: new List<WindowRect>(),
                                 screenWidth: 1920, screenHeight: 1080);
        Assert.Equal(new WindowRect(80, 90, 320, 150), moved);
    }

    [Fact] public void UpdateDrag_NearScreenEdgeFraction_Snaps()
    {
        var s = NewSvc(); s.ToggleEditMode(); s.SelectWindow("p.main");
        s.BeginDrag(0, 0, new WindowRect(2, 0, 320, 150));  // 2px right of left edge
        // Move 1px right (total x=3); within 6px snap threshold to x=0.
        var snapped = s.UpdateDrag(1, 0, new List<WindowRect>(), 1920, 1080);
        Assert.Equal(0f, snapped.X);
    }

    [Fact] public void UpdateDrag_NearOtherWindowEdge_Snaps()
    {
        var s = NewSvc(); s.ToggleEditMode(); s.SelectWindow("p.main");
        var other = new WindowRect(500, 100, 300, 200);   // right edge at x=800
        s.BeginDrag(0, 0, new WindowRect(795, 100, 320, 150));  // left edge near other's right
        var snapped = s.UpdateDrag(0, 0, new List<WindowRect> { other }, 1920, 1080);
        Assert.Equal(800f, snapped.X);
    }

    [Fact] public void EndDrag_ClearsDragState_PersistsRect()
    {
        var storage = MakeStorage();
        var s = NewSvc(storage); s.ToggleEditMode(); s.SelectWindow("p.main");
        s.BeginDrag(0, 0, new WindowRect(50, 60, 320, 150));
        var moved = s.UpdateDrag(100, 100, new List<WindowRect>(), 1920, 1080);
        s.EndDrag(finalRect: moved, currentResolution: new Resolution(1920, 1080));

        Assert.False(s.IsDragging);
        var saved = storage.Get(0, "p.main", new Resolution(1920, 1080), new WindowRect(0, 0, 0, 0));
        Assert.Equal(moved, saved.Rect);
    }

    [Fact] public void UpdateDrag_BelowMinSize_NotApplied()
    {
        var s = NewSvc(); s.ToggleEditMode(); s.SelectWindow("p.main");
        s.BeginDrag(0, 0, new WindowRect(0, 0, 320, 150));
        // Resize would shrink below 80x40 min - caller's responsibility to clamp;
        // service just provides the snap math. This test documents the invariant
        // that the SERVICE does NOT clamp size; the overlay clamps before calling.
        // So passing a tiny rect through UpdateDrag (which is move-only) still
        // returns the moved rect with the same w/h:
        var moved = s.UpdateDrag(50, 50, new List<WindowRect>(), 1920, 1080);
        Assert.Equal(320f, moved.Width);
        Assert.Equal(150f, moved.Height);
    }

    private static LayoutEditorService NewSvc(LayoutStorage? storage = null)
        => new(storage ?? MakeStorage(), new NullLog());

    private static LayoutStorage MakeStorage()
        => new(new InMemoryConfig(), new NullLog());

    private sealed class InMemoryConfig : IPluginConfig
    {
        private readonly Dictionary<string, InMemorySection> _sec = new();
#pragma warning disable CS0067
        public event System.Action<string>? SectionChanged;
#pragma warning restore CS0067
        public IConfigSection GetSection(string name)
        {
            if (!_sec.TryGetValue(name, out var s)) _sec[name] = s = new InMemorySection();
            return s;
        }
    }
    private sealed class InMemorySection : IConfigSection
    {
        private readonly Dictionary<string, object?> _s = new();
        public T? Get<T>(string k, T? d) => _s.TryGetValue(k, out var v) && v is T t ? t : d;
        public void Set<T>(string k, T v) => _s[k] = v;
        public void Save() { }
        public void SaveQuiet() { }
    }
    private sealed class NullLog : IPluginLog
    {
        public void Info(string m)    { }
        public void Warning(string m) { }
        public void Error(string m)   { }
        public void Debug(string m)   { }
    }
}
