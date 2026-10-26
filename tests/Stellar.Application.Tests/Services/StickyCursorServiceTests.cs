using System.Collections.Generic;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public sealed class StickyCursorServiceTests
{
    private sealed class FakeBackend : IStickyCursorBackend
    {
        public bool LuaReady { get; set; } = true;
        public readonly List<string> Calls = new();
        public bool CursorRequested, EscBlocked, MouseBlocked;
        public void RequestGameCursor(bool on) { CursorRequested = on; Calls.Add(on ? "add" : "remove"); }
        public void SetEscapeBlocked(bool on) { EscBlocked = on; Calls.Add(on ? "esc+" : "esc-"); }
        public void SetWorldMouseBlocked(bool on) { MouseBlocked = on; Calls.Add(on ? "mouse+" : "mouse-"); }
    }

    // A steady frame: setting on, in world, cursor free, an interactive window up, pointer over it, nothing pressed.
    private static StickyCursorFrame Idle(StickyPointer pointer = StickyPointer.StellarInteractive) => new()
    {
        Enabled = true, WorldActive = true, CursorFree = true, AnyInteractiveVisible = true, Pointer = pointer,
    };

    private static StickyCursorFrame Press(StickyPointer pointer)
    {
        var f = Idle(pointer);
        f.MouseDown = true;
        f.AnyMouseHeld = true;
        return f;
    }

    private static (StickyCursorService Svc, FakeBackend Backend) Engaged()
    {
        var b = new FakeBackend();
        var s = new StickyCursorService(b);
        s.Tick(Press(StickyPointer.StellarInteractive));
        s.Tick(Idle());
        Assert.True(s.IsSticky);
        return (s, b);
    }

    [Fact]
    public void Click_on_interactive_window_with_free_cursor_engages_and_blocks_escape()
    {
        var (_, b) = Engaged();
        Assert.True(b.CursorRequested);
        Assert.True(b.EscBlocked);
        Assert.False(b.MouseBlocked);   // pointer is over the window, not the world
    }

    [Theory]
    [InlineData((int)StickyPointer.StellarHud)]
    [InlineData((int)StickyPointer.GameUi)]
    [InlineData((int)StickyPointer.World)]
    public void Click_elsewhere_does_not_engage(int pointer)   // int: StickyPointer is internal, a public test method can't take it
    {
        var b = new FakeBackend();
        var s = new StickyCursorService(b);
        s.Tick(Press((StickyPointer)pointer));
        Assert.False(s.IsSticky);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void Does_not_engage_when_disabled_hidden_cursor_or_lua_not_ready()
    {
        var b = new FakeBackend();
        var s = new StickyCursorService(b);
        var off = Press(StickyPointer.StellarInteractive); off.Enabled = false;
        var locked = Press(StickyPointer.StellarInteractive); locked.CursorFree = false;
        s.Tick(off);
        s.Tick(locked);
        b.LuaReady = false;
        s.Tick(Press(StickyPointer.StellarInteractive));
        Assert.False(s.IsSticky);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void Pointer_over_world_pre_blocks_mouse_and_world_click_releases_then_unblocks_on_button_up()
    {
        var (s, b) = Engaged();
        s.Tick(Idle(StickyPointer.World));
        Assert.True(b.MouseBlocked);                  // armed BEFORE the press reaches the game
        s.Tick(Press(StickyPointer.World));
        Assert.False(s.IsSticky);
        Assert.False(b.CursorRequested);
        Assert.True(b.MouseBlocked);                  // still held → still swallowed
        var held = Idle(StickyPointer.World); held.AnyMouseHeld = true;
        s.Tick(held);
        Assert.True(b.MouseBlocked);
        s.Tick(Idle(StickyPointer.World));
        Assert.False(b.MouseBlocked);                 // released → game gets the mouse back
    }

    [Fact]
    public void Mouse_stays_blocked_through_the_button_up_frame_then_unblocks()
    {
        var (s, b) = Engaged();
        s.Tick(Idle(StickyPointer.World));
        s.Tick(Press(StickyPointer.World));
        Assert.False(s.IsSticky);
        // The up frame: no button held any more, but the game may still read this frame's release (picking selects on it).
        var up = Idle(StickyPointer.World); up.AnyMouseUp = true;
        s.Tick(up);
        Assert.True(b.MouseBlocked);
        s.Tick(Idle(StickyPointer.World));
        Assert.False(b.MouseBlocked);
    }

    [Fact]
    public void Button_up_alone_never_arms_the_mouse_block()
    {
        var (s, b) = Engaged();
        var up = Idle(StickyPointer.GameUi); up.AnyMouseUp = true;   // e.g. the release of a click on game UI
        s.Tick(up);
        Assert.False(b.MouseBlocked);
    }

    [Fact]
    public void Clicks_on_game_ui_and_hud_keep_sticky()
    {
        var (s, b) = Engaged();
        s.Tick(Press(StickyPointer.GameUi));
        s.Tick(Press(StickyPointer.StellarHud));
        Assert.True(s.IsSticky);
        Assert.False(b.MouseBlocked);
    }

    [Fact]
    public void Escape_releases_and_stays_blocked_until_key_up()
    {
        var (s, b) = Engaged();
        var esc = Idle(); esc.EscDown = true; esc.EscHeld = true;
        s.Tick(esc);
        Assert.False(s.IsSticky);
        Assert.True(b.EscBlocked);
        var held = Idle(); held.EscHeld = true;
        s.Tick(held);
        Assert.True(b.EscBlocked);
        s.Tick(Idle());
        Assert.False(b.EscBlocked);
    }

    [Fact]
    public void Escape_owned_by_a_focused_field_keeps_sticky()
    {
        var (s, _) = Engaged();
        var esc = Idle(); esc.EscDown = true; esc.EscHeld = true; esc.FieldFocused = true;
        s.Tick(esc);
        Assert.True(s.IsSticky);
    }

    [Fact]
    public void Setting_off_or_no_interactive_window_releases()
    {
        var (s, b) = Engaged();
        var off = Idle(); off.Enabled = false;
        s.Tick(off);
        Assert.False(s.IsSticky);
        Assert.False(b.CursorRequested);

        (s, b) = Engaged();
        var none = Idle(); none.AnyInteractiveVisible = false;
        s.Tick(none);
        Assert.False(s.IsSticky);
        Assert.False(b.CursorRequested);
    }

    [Fact]
    public void World_inactive_releases_and_defers_the_game_call_until_the_world_is_back()
    {
        var (s, b) = Engaged();
        var loading = Idle(); loading.WorldActive = false;
        s.Tick(loading);
        Assert.False(s.IsSticky);
        Assert.True(b.CursorRequested);     // not called outside the world
        Assert.False(b.EscBlocked);
        s.Tick(loading);
        Assert.True(b.CursorRequested);
        s.Tick(Idle());
        Assert.False(b.CursorRequested);    // drained on the first world-active tick
    }

    [Fact]
    public void Host_lost_releases_everything()
    {
        var (s, b) = Engaged();
        s.Tick(Idle(StickyPointer.World));
        s.OnHostLost();
        Assert.False(s.IsSticky);
        Assert.False(b.CursorRequested);
        Assert.False(b.EscBlocked);
        Assert.False(b.MouseBlocked);
    }
}
