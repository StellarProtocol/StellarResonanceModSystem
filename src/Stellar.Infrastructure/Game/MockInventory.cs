using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Deterministic <see cref="IInventory"/> implementation activated by the
/// <c>STELLAR_MOCK_INVENTORY=1</c> environment variable. Returns a fixed
/// 12-module package covering Attack / Assistant / Defend categories with
/// realistic basic + special attribute combinations so the ModuleOptimizer
/// plugin can render Targets + Results outside of a real session for the
/// Phase 9a.5 visual verification toolkit.
///
/// The fixture is non-trivial on purpose: every module has 3 parts, attribute
/// IDs are drawn from <c>CombatPower.BasicAttrIds</c> /
/// <c>CombatPower.SpecialAttrIds</c>, and per-part values span 1..6 so the
/// optimiser engine produces a meaningful score spread when the user clicks
/// Optimize. Without this richness Results renders as a flat zero score.
///
/// The snapshot is static — <see cref="InventoryChanged"/> never fires and
/// equipped-set is empty. Visual scenarios open Targets / Results, capture
/// the rendered chrome, and exit; nothing exercises change-detection or
/// equip flow.
///
/// Production code path is unaffected when the env var is absent — the mock
/// is wired in <c>BootstrapPlugin.WireGameEventsAndPluginHost</c> only when
/// the env var equals <c>"1"</c>. The production <c>PandaInventoryProbe</c>
/// + <c>InventoryService</c> still construct and tick; the mock simply
/// replaces what plugins see via <see cref="IPluginServices.Inventory"/>.
/// </summary>
internal sealed class MockInventory : IInventory
{
    // ---------------------------------------------------------------------
    // Attribute-id labels (display names). Mirrors the shape the production
    // probe emits when ModulePart.Name is resolved via
    // IGameDataCombat.GetAttribute — we use English approximations so the
    // rendered UI is readable without the game-data tables loaded.
    // ---------------------------------------------------------------------
    private const int BasicStr        = 1110;
    private const int BasicDex        = 1111;
    private const int BasicInt        = 1112;
    private const int BasicVit        = 1113;
    private const int BasicEle        = 1114;
    private const int SpecialAtk1     = 2104;
    private const int SpecialAtk2     = 2105;
    private const int SpecialAsi1     = 2204;
    private const int SpecialAsi2     = 2205;
    private const int SpecialDef1     = 2304;

    private static readonly IReadOnlyList<ModuleInfo> Fixture = BuildFixture();

    private static ModuleInfo[] BuildFixture()
    {
        var attack = new[]
        {
            Make(H(1001, 4001, "Stormblade Core", 4, ModuleCategory.Attack), P(BasicStr, "Strength", 6), P(BasicDex, "Dexterity", 3), P(SpecialAtk1, "Crit Power", 2)),
            Make(H(1002, 4002, "Ember Core",      3, ModuleCategory.Attack), P(BasicStr, "Strength", 4), P(BasicEle, "Elemental", 2), P(SpecialAtk2, "Crit Rate",  1)),
            Make(H(1003, 4003, "Frost Core",      3, ModuleCategory.Attack), P(BasicInt, "Intellect",5), P(BasicEle, "Elemental", 4), P(SpecialAtk1, "Crit Power", 1)),
            Make(H(1004, 4004, "Tempest Core",    4, ModuleCategory.Attack), P(BasicStr, "Strength", 5), P(BasicDex, "Dexterity", 5), P(SpecialAtk2, "Crit Rate",  2)),
        };
        var assist = new[]
        {
            Make(H(1005, 4005, "Verdant Sigil",   3, ModuleCategory.Assistant), P(BasicInt, "Intellect",6), P(BasicVit, "Vitality",  2), P(SpecialAsi1, "Haste",    1)),
            Make(H(1006, 4006, "Mending Sigil",   4, ModuleCategory.Assistant), P(BasicInt, "Intellect",4), P(BasicVit, "Vitality",  4), P(SpecialAsi2, "Recovery", 3)),
            Make(H(1007, 4007, "Tide Sigil",      3, ModuleCategory.Assistant), P(BasicInt, "Intellect",5), P(BasicEle, "Elemental", 3), P(SpecialAsi1, "Haste",    2)),
            Make(H(1008, 4008, "Aurora Sigil",    4, ModuleCategory.Assistant), P(BasicInt, "Intellect",6), P(BasicVit, "Vitality",  3), P(SpecialAsi2, "Recovery", 1)),
        };
        var defend = new[]
        {
            Make(H(1009, 4009, "Bastion Ward",    4, ModuleCategory.Defend), P(BasicVit, "Vitality", 6), P(BasicStr, "Strength",  2), P(SpecialDef1, "Toughness", 2)),
            Make(H(1010, 4010, "Bulwark Ward",    3, ModuleCategory.Defend), P(BasicVit, "Vitality", 5), P(BasicDex, "Dexterity", 2), P(SpecialDef1, "Toughness", 1)),
            Make(H(1011, 4011, "Aegis Ward",      4, ModuleCategory.Defend), P(BasicVit, "Vitality", 4), P(BasicEle, "Elemental", 3), P(SpecialDef1, "Toughness", 3)),
            Make(H(1012, 4012, "Vanguard Ward",   3, ModuleCategory.Defend), P(BasicVit, "Vitality", 6), P(BasicStr, "Strength",  1), P(SpecialDef1, "Toughness", 1)),
        };

        var all = new ModuleInfo[attack.Length + assist.Length + defend.Length];
        attack.CopyTo(all, 0);
        assist.CopyTo(all, attack.Length);
        defend.CopyTo(all, attack.Length + assist.Length);
        return all;
    }

    private static readonly ModuleSnapshot SnapshotInstance =
        new ModuleSnapshot(Fixture, ServerSampledAtTicks: 0);

    private static readonly EquippedSet EmptyEquipped =
        new EquippedSet(new Dictionary<int, long>());

    public bool IsAvailable => true;
    public ModuleSnapshot? GetModules() => SnapshotInstance;
    public EquippedSet? GetEquipped() => EmptyEquipped;

    // The mock fixture carries no gear — plugins see the same empty-until-sync
    // shape the production surface presents before the first full sync.
    public IReadOnlyList<GearInstance> GetSelfGear() => Array.Empty<GearInstance>();

    // Static fixture — InventoryChanged never fires. Add/remove are no-ops so
    // plugins that subscribe (e.g. ModuleOptimizer's Targets watcher) still
    // compile and behave correctly.
    public event Action? InventoryChanged
    {
        add { /* intentional no-op */ }
        remove { /* intentional no-op */ }
    }

    private static ModulePart P(int attrId, string label, int value) =>
        new ModulePart(attrId, label, value);

    // Header bundle: identity + display + category in a single param so the
    // Make factory stays under the 5-param analyzer cap (STELLAR0003) while
    // keeping the per-row literal in BuildFixture readable.
    private readonly record struct Hdr(long Uuid, int ConfigId, string Name, int Quality, ModuleCategory Category);

    private static Hdr H(long uuid, int configId, string name, int quality, ModuleCategory category) =>
        new Hdr(uuid, configId, name, quality, category);

    private static ModuleInfo Make(in Hdr h, params ModulePart[] parts)
        => new ModuleInfo(h.Uuid, h.ConfigId, h.Name, h.Quality, h.Category, parts);
}
