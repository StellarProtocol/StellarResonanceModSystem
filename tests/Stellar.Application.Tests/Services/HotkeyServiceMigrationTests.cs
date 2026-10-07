using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Stellar.Application.Tests.Theme;
using Xunit;

namespace Stellar.Application.Tests.Services;

// Spec 2026-10-01 free camera D7 (owner): free camera = Alt+F10, hide-all moves to Ctrl+F10, a saved Alt+F10 is migrated
// once, and no chord is ever held by two actions — a saved binding beats a default whatever the declare order.
public sealed class HotkeyServiceMigrationTests
{
    private static readonly KeyBinding AltF10 = new(StellarKeyCode.F10, ModifierKeys.Alt);
    private static readonly KeyBinding CtrlF10 = new(StellarKeyCode.F10, ModifierKeys.Ctrl);

    private static (HotkeyService Svc, InMemoryConfigSection Cfg) Make(params (string Id, KeyBinding? Saved)[] saved)
    {
        var cfg = new InMemoryConfigSection();
        foreach (var (id, b) in saved)
        {
            cfg.Values[$"bindings.{id}.key"] = b is { } k ? k.Key.ToString() : "_unbound_";
            cfg.Values[$"bindings.{id}.mods"] = b is { } m ? m.Modifiers.ToString() : "None";
        }
        return (new HotkeyService(new NoInput(), new NoLog(), cfg), cfg);
    }

    private static IHotkeyAction Declare(HotkeyService svc, string id, KeyBinding? def) =>
        svc.DeclareAction(new HotkeyAction(id, id, def), () => { });

    private static int Holders(HotkeyService svc, KeyBinding b) =>
        ((IHotkeyDirectory)svc).Actions.Count(a => a.CurrentBinding == b);

    [Fact]
    public void Contract_IHotkeys_members_and_stable_outcomes()
    {
        // 2.20.0 added IsActionHeld (default-implemented, so existing test doubles still compile).
        Assert.Equal(new[] { "DeclareAction", "IsActionHeld", "MigrateSavedBinding" },
            typeof(IHotkeys).GetMethods().Select(m => m.Name).OrderBy(n => n, System.StringComparer.Ordinal));
        Assert.Equal(new[] { "NothingSaved", "KeptOther", "Moved", "Cleared" }, System.Enum.GetNames(typeof(SavedBindingMigration)));
    }

    [Fact]
    public void A_saved_old_chord_moves_to_the_new_chord_and_is_persisted()
    {
        var (svc, cfg) = Make(("photostudio.hideall", AltF10));
        Assert.Equal(SavedBindingMigration.Moved, svc.MigrateSavedBinding("photostudio.hideall", AltF10, CtrlF10));
        Assert.Equal("F10", cfg.Values["bindings.photostudio.hideall.key"]);
        Assert.Equal("Ctrl", cfg.Values["bindings.photostudio.hideall.mods"]);
        Assert.Equal(CtrlF10, Declare(svc, "photostudio.hideall", CtrlF10).CurrentBinding);
    }

    [Fact]
    public void Nothing_saved_reports_NothingSaved_and_writes_nothing()
    {
        var (svc, cfg) = Make();
        Assert.Equal(SavedBindingMigration.NothingSaved, svc.MigrateSavedBinding("photostudio.hideall", AltF10, CtrlF10));
        Assert.Empty(cfg.Values);
    }

    [Fact]
    public void A_different_saved_choice_or_a_saved_unbound_is_kept()
    {
        var (svc, cfg) = Make(("a.hide", new KeyBinding(StellarKeyCode.F9)), ("b.hide", null));
        Assert.Equal(SavedBindingMigration.KeptOther, svc.MigrateSavedBinding("a.hide", AltF10, CtrlF10));
        Assert.Equal(SavedBindingMigration.KeptOther, svc.MigrateSavedBinding("b.hide", AltF10, CtrlF10));
        Assert.Equal("F9", cfg.Values["bindings.a.hide.key"]);
        Assert.Equal("_unbound_", cfg.Values["bindings.b.hide.key"]);
    }

    [Fact]
    public void A_target_chord_held_by_another_action_saves_unbound_instead_of_a_duplicate()
    {
        var (svc, cfg) = Make(("photostudio.hideall", AltF10));
        Declare(svc, "combatmeter.reset", CtrlF10);
        Assert.Equal(SavedBindingMigration.Cleared, svc.MigrateSavedBinding("photostudio.hideall", AltF10, CtrlF10));
        Assert.Equal("_unbound_", cfg.Values["bindings.photostudio.hideall.key"]);
        Assert.Null(Declare(svc, "photostudio.hideall", CtrlF10).CurrentBinding);
        Assert.Equal(1, Holders(svc, CtrlF10));
    }

    [Fact]
    public void Migrating_an_already_declared_action_updates_its_live_binding()
    {
        var (svc, _) = Make(("photostudio.hideall", AltF10));
        var hide = Declare(svc, "photostudio.hideall", CtrlF10);
        Assert.Equal(AltF10, hide.CurrentBinding);
        var changed = new List<string>();
        svc.BindingChanged += changed.Add;
        svc.MigrateSavedBinding("photostudio.hideall", AltF10, CtrlF10);
        Assert.Equal(CtrlF10, hide.CurrentBinding);
        Assert.Equal(new[] { "photostudio.hideall" }, changed);
    }

    [Fact]
    public void A_saved_binding_declared_later_takes_the_chord_from_a_default()
    {
        var (svc, _) = Make(("photostudio.hideall", AltF10));
        var free = Declare(svc, "photostudio.freecam", AltF10);   // alphabetically first, but only a default
        var hide = Declare(svc, "photostudio.hideall", CtrlF10);
        Assert.Null(free.CurrentBinding);
        Assert.Equal(AltF10, hide.CurrentBinding);
        Assert.Equal(1, Holders(svc, AltF10));
    }

    [Fact]
    public void A_default_never_takes_a_saved_chord_even_when_alphabetically_first()
    {
        var (svc, _) = Make(("zz.saved", AltF10));
        var saved = Declare(svc, "zz.saved", null);
        var dflt = Declare(svc, "aa.default", AltF10);
        Assert.Equal(AltF10, saved.CurrentBinding);
        Assert.Null(dflt.CurrentBinding);
    }

    [Fact]
    public void Two_saved_claims_on_one_chord_resolve_alphabetically()
    {
        var (svc, _) = Make(("zz.second", AltF10), ("aa.first", AltF10));
        var z = Declare(svc, "zz.second", null);
        var a = Declare(svc, "aa.first", null);
        Assert.Equal(AltF10, a.CurrentBinding);
        Assert.Null(z.CurrentBinding);
        Assert.Equal(1, Holders(svc, AltF10));
    }

    [Fact]
    public void Photo_Studio_1_0_upgrade_ends_with_exactly_one_action_per_chord()
    {
        var (svc, _) = Make(("photostudio.hideall", AltF10));
        var scoped = new PerPluginHotkeys("stellar.photostudio", svc);
        Assert.Equal(SavedBindingMigration.Moved, scoped.MigrateSavedBinding("photostudio.hideall", AltF10, CtrlF10));
        var free = scoped.DeclareAction(new HotkeyAction("photostudio.freecam", "free", AltF10), () => { });
        var hide = scoped.DeclareAction(new HotkeyAction("photostudio.hideall", "hide", CtrlF10), () => { });
        Assert.Equal(AltF10, free.CurrentBinding);
        Assert.Equal(CtrlF10, hide.CurrentBinding);
        Assert.Equal(1, Holders(svc, AltF10));
        Assert.Equal(1, Holders(svc, CtrlF10));
    }

    private sealed class NoInput : IInputGateway
    {
        public IReadOnlyList<StellarKeyCode> PressedKeysThisFrame => System.Array.Empty<StellarKeyCode>();
        public ModifierKeys CurrentModifiers => ModifierKeys.None;
        public bool IsKeyHeld(StellarKeyCode key) => false;
        public Resolution CurrentResolution => new(1920, 1080);
        public (float X, float Y) PointerPosition => (0f, 0f);
        public bool LeftMouseDown => false;
        public bool LeftMousePressedSinceTick => false;
        public int CurrentFrame => 1;
    }

    private sealed class NoLog : IPluginLog
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
        public void Debug(string message) { }
    }
}
