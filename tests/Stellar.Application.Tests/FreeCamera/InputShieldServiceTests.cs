using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Spec § 6 IInputShield: reference-counted over the EGm ignore mask; reads come from the raw input reader; while any
// handle is held the keyboard gate blocks every game key (spec D8, owner 2026-10-01).
public sealed class InputShieldServiceTests
{
    private sealed class FakeBackend : IInputShieldBackend
    {
        public bool Ok = true;
        public readonly List<bool> Calls = new();
        public readonly List<(bool Camera, bool Pause)> Layers = new();
        public int Forgets;
        public bool Apply(bool camera, bool pause) { Calls.Add(camera); Layers.Add((camera, pause)); return Ok; }
        public void Forget() => Forgets++;
    }

    private sealed class FakeReader : IShieldInputReader
    {
        public bool IsHeld(StellarKeyCode key) => key == StellarKeyCode.W;
        public ModifierKeys Modifiers => ModifierKeys.Shift;
        public bool IsMouseHeld(int button) => button == 1;
        public (float X, float Y) MouseDelta => (3f, -2f);
        public float Wheel => 1f;
        public (float X, float Y) Pointer => (100f, 200f);
    }

    private sealed class FakeFocus : ITextFieldFocus
    {
        public bool Focused;
        public bool AnyFieldFocused => Focused;
    }

    private static (InputShieldService Svc, FakeBackend B, List<string> Warn) Make() => Make(new FakeFocus());

    private static (InputShieldService Svc, FakeBackend B, List<string> Warn) Make(FakeFocus focus)
    {
        var b = new FakeBackend();
        var warn = new List<string>();
        return (new InputShieldService(b, new FakeReader(), focus, warn.Add), b, warn);
    }

    // Review F1: typing into a panel search box must not drive the free camera — the handle exposes the same text-field
    // focus the keyboard gate reads, live, and reports false once disposed.
    [Fact]
    public void TextFieldFocused_reports_the_focus_source_while_held_and_false_after_dispose()
    {
        var focus = new FakeFocus();
        var (svc, _, _) = Make(focus);
        var h = svc.Shield();
        Assert.False(h.TextFieldFocused);
        focus.Focused = true;
        Assert.True(h.TextFieldFocused);
        focus.Focused = false;
        Assert.False(h.TextFieldFocused);
        focus.Focused = true;
        h.Dispose();
        Assert.False(h.TextFieldFocused);
    }

    [Fact]
    public void Two_handles_raise_the_mask_once_and_the_last_release_drops_it()
    {
        var (svc, b, _) = Make();
        var h1 = svc.Shield();
        var h2 = svc.Shield();
        Assert.Equal(new[] { true }, b.Calls);
        Assert.True(svc.IsShielded);
        h1.Dispose();
        Assert.Equal(new[] { true }, b.Calls);
        h2.Dispose();
        h2.Dispose();
        Assert.Equal(new[] { true, false }, b.Calls);
        Assert.False(svc.IsShielded);
    }

    [Fact]
    public void Handle_reads_pass_through_while_active_and_default_after_dispose()
    {
        var (svc, _, _) = Make();
        var h = svc.Shield();
        Assert.True(h.IsHeld(StellarKeyCode.W));
        Assert.Equal(ModifierKeys.Shift, h.Modifiers);
        Assert.True(h.IsMouseHeld(1));
        Assert.Equal((3f, -2f), h.MouseDelta);
        Assert.Equal(1f, h.Wheel);
        Assert.Equal((100f, 200f), h.Pointer);
        h.Dispose();
        Assert.False(h.IsActive);
        Assert.False(h.IsHeld(StellarKeyCode.W));
        Assert.Equal(ModifierKeys.None, h.Modifiers);
        Assert.Equal((0f, 0f), h.MouseDelta);
        Assert.Equal(0f, h.Wheel);
    }

    [Fact]
    public void Reassert_reissues_only_while_held()
    {
        var (svc, b, _) = Make();
        svc.Reassert();
        Assert.Empty(b.Calls);
        var h = svc.Shield();
        svc.Reassert();
        Assert.Equal(new[] { true, true }, b.Calls);
        h.Dispose();
    }

    [Fact]
    public void A_failed_mask_call_warns_once_and_reports_not_shielded()
    {
        var (svc, b, warn) = Make();
        b.Ok = false;
        var h1 = svc.Shield();
        var h2 = svc.Shield();
        Assert.False(svc.IsShielded);
        Assert.Single(warn);
        Assert.True(h1.IsActive);            // reads still work, so the camera still moves
        h1.Dispose();
        h2.Dispose();
    }

    [Fact]
    public void ReleaseOwner_and_facade_release_only_that_plugins_handles()
    {
        var (svc, b, _) = Make();
        var mine = new PluginInputShield(svc, new object());
        var theirs = svc.Shield(new object());
        var h = mine.Shield();
        mine.ReleaseAll();
        Assert.False(h.IsActive);
        Assert.True(theirs.IsActive);
        Assert.True(svc.IsShielded);
        theirs.Dispose();
        Assert.Equal(new[] { true, false }, b.Calls);
    }

    [Fact]
    public void ReleaseAll_drops_every_handle()
    {
        var (svc, b, _) = Make();
        var a = svc.Shield();
        var c = svc.Shield();
        svc.ReleaseAll();
        Assert.False(a.IsActive);
        Assert.False(c.IsActive);
        Assert.Equal(new[] { true, false }, b.Calls);
    }

    [Fact]
    public void Keyboard_is_blocked_while_any_handle_is_held_even_when_the_mask_failed()
    {
        var (svc, b, _) = Make();
        b.Ok = false;                        // the mask is down, but Esc / Enter / M must still not reach the game
        Assert.False(svc.KeyboardBlocked);
        var h1 = svc.Shield();
        var h2 = svc.Shield();
        Assert.True(svc.KeyboardBlocked);
        h1.Dispose();
        Assert.True(svc.KeyboardBlocked);
        h2.Dispose();
        Assert.False(svc.KeyboardBlocked);
    }

    [Fact]
    public void KeyboardBlockChanged_fires_on_the_first_raise_and_the_last_release_only()
    {
        var (svc, _, _) = Make();
        var seen = new List<bool>();
        svc.KeyboardBlockChanged += () => seen.Add(svc.KeyboardBlocked);
        var h1 = svc.Shield();
        var h2 = svc.Shield();
        h1.Dispose();
        h2.Dispose();
        var h3 = svc.Shield();
        svc.ReleaseAll();
        Assert.Equal(new[] { true, false, true, false }, seen);
    }

    // Review M-4 (2026-10-03): the pause block used to sit under the shop's EPayWebView source (which the game's payment
    // callback may clear wholesale). Now ONE source holds the union of the free camera's mask and the pause block: every change
    // re-applies both layers, so dropping the camera keeps the pause block and unfreezing keeps the camera's mask. Do not weaken.
    [Fact]
    public void freeze_pause_block_and_free_camera_share_one_source_as_a_union()
    {
        var (svc, b, _) = Make();
        svc.SetPauseBlock(true);                                 // freeze first
        var h = svc.Shield();                                    // then the free camera
        h.Dispose();                                             // Esc out of the free camera: the pause block stays
        svc.SetPauseBlock(true);                                 // no change: nothing re-applied
        svc.SetPauseBlock(false);                                // unfreeze
        Assert.Equal(new[] { (false, true), (true, true), (false, true), (false, false) }, b.Layers);
        Assert.False(svc.KeyboardBlocked);                       // the pause block never blocks the keyboard

        b.Layers.Clear();
        var c = svc.Shield();                                    // free camera first
        svc.SetPauseBlock(true);
        svc.SetPauseBlock(false);                                // unfreeze while the camera is still on: its mask stays
        Assert.True(svc.IsShielded);
        c.Dispose();
        Assert.Equal(new[] { (true, false), (true, true), (true, false), (false, false) }, b.Layers);
    }

    [Fact]
    public void freeze_reassert_reissues_the_pause_block_alone_after_a_zone_load()
    {
        var (svc, b, _) = Make();
        svc.SetPauseBlock(true);
        svc.Reassert();
        Assert.Equal(1, b.Forgets);                              // the game may have rebuilt its table: re-set every wanted bit
        Assert.Equal(new[] { (false, true), (false, true) }, b.Layers);
        svc.SetPauseBlock(false);
        svc.Reassert();                                          // nothing held: no call
        Assert.Equal(1, b.Forgets);
        Assert.Equal(3, b.Layers.Count);
    }
}
