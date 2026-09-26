using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Pins the spec-root-buff map (spec-from-talent-buffs, owner go 2026-09-26). Derived at runtime from the
/// game's own tables: <c>TalentStageTable</c> (<c>TalentStage == 1</c>) → <c>RootId</c> (TalentTreeTable id)
/// → its <c>TalentId</c> → <c>TalentTable.TalentEffect</c> <c>[3, buffId, level]</c>; spec id =
/// <c>WeaponType*10000 + BdType + 1</c> (the <c>ProfessionSpecs</c> format). If the derivation is not 18 entries
/// the 18 release_3.7 constants below are used instead.
/// </summary>
public sealed class SpecRootBuffsTests
{
    private static readonly Dictionary<int, int> Expected = new()
    {
        { 2200320, 10001 },  { 2200590, 10002 },    // Stormblade: Iaido / Moonstrike
        { 2204300, 20001 },  { 2204120, 20002 },    // Frost Mage: Icicle / Frostbeam
        { 2208130, 30001 },  { 2208430, 30002 },    // Twin Striker: Formless / Crimson
        { 2205300, 40001 },  { 2205290, 40002 },    // Wind Knight: Vanguard / Skyward
        { 2202110, 50001 },  { 2202340, 50002 },    // Verdant Oracle: Smite / Lifebind
        { 2201330, 90001 },  { 2201320, 90002 },    // Heavy Guardian: Earthfort / Block
        { 2203260, 110001 }, { 2203290, 110002 },   // Marksman: Wildpack / Falconry
        { 2206090, 120001 }, { 2206190, 120002 },   // Shield Knight: Recovery / Shield
        { 2207090, 130001 }, { 2207180, 130002 },   // Beat Performer: Dissonance / Concerto
    };

    [Fact]
    public void Fallback_IsExactlyThe18Release37Constants()
    {
        Assert.Equal(18, SpecRootBuffs.Fallback.Count);
        Assert.Equal(Expected.OrderBy(k => k.Key), SpecRootBuffs.Fallback.OrderBy(k => k.Key));
    }

    // Builds a fake table set shaped like release_3.7 (plus noise) from a buff→spec map.
    private static SpecTalentTables FakeTables(IReadOnlyDictionary<int, int> rootBuffToSpec)
    {
        var stages = new Dictionary<int, TalentStageRow>();
        var trees = new Dictionary<int, int>();
        var talents = new Dictionary<int, TalentEffectRow>();
        int stageId = 100, treeId = 500_000, talentId = 9000;
        foreach (var (buff, spec) in rootBuffToSpec)
        {
            int wt = spec / 10000, bd = spec % 10000 - 1;
            // Expertise I tree for the class (stage 0) — must be ignored even though it grants a buff.
            stages[stageId++] = new TalentStageRow(wt, bd, 0, treeId);
            trees[treeId++] = talentId;
            talents[talentId++] = new TalentEffectRow(new[] { new[] { 3, 1_000_000 + spec, 1 } });
            // Expertise II root: a non-buff effect first, then the grant-buff effect.
            stages[stageId++] = new TalentStageRow(wt, bd, 1, treeId);
            trees[treeId++] = talentId;
            talents[talentId++] = new TalentEffectRow(new[] { new[] { 1, 11012, 10 }, new[] { 3, buff, 1 } });
        }
        // Transform "classes" (8/14/15) carry RootId 0 — ignored.
        stages[stageId++] = new TalentStageRow(8, 0, 1, 0);
        stages[stageId]   = new TalentStageRow(14, 1, 1, 0);
        return new SpecTalentTables(stages, trees, talents);
    }

    [Fact]
    public void Derive_FromFakeTables_ReproducesMap()
    {
        var derived = SpecRootBuffs.Derive(FakeTables(Expected));
        Assert.Equal(Expected.OrderBy(k => k.Key), derived.OrderBy(k => k.Key));
    }

    [Fact]
    public void Derive_SmallSet_OnlyStage1RootsCount()
    {
        var two = new Dictionary<int, int> { { 111, 50001 }, { 222, 50002 } };
        var derived = SpecRootBuffs.Derive(FakeTables(two));
        Assert.Equal(two.OrderBy(k => k.Key), derived.OrderBy(k => k.Key));
    }

    [Fact]
    public void Derive_MissingTreeOrTalent_SkipsThatStage()
    {
        var t = FakeTables(new Dictionary<int, int> { { 111, 50001 }, { 222, 50002 } });
        var trees = new Dictionary<int, int>(t.TreeTalentIds);
        var rootOf222 = t.Stages.Values.Single(s => s.TalentStage == 1 && s.BdType == 1 && s.WeaponType == 5).RootId;
        trees.Remove(rootOf222);
        var derived = SpecRootBuffs.Derive(t with { TreeTalentIds = trees });
        Assert.Equal(new[] { 111 }, derived.Keys);
    }

    // Staged source (perf G): one table per LoadStep, the tree/effect reads filtered to the ids the previous
    // stage needs. Fake serves a SpecTalentTables and records what was asked for.
    private sealed class FakeSource : ISpecRootBuffSource
    {
        public SpecTalentTables? Tables;
        public Exception? Throw;
        public List<int> TreeIdsAsked = new(), TalentIdsAsked = new();
        public int Calls;

        public IReadOnlyDictionary<int, TalentStageRow> ReadTalentStages()
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Tables?.Stages ?? new Dictionary<int, TalentStageRow>();
        }

        public IReadOnlyDictionary<int, int> ReadTalentTreeTalentIds(IReadOnlyCollection<int> treeIds)
        {
            Calls++;
            TreeIdsAsked.AddRange(treeIds);
            return (Tables?.TreeTalentIds ?? new Dictionary<int, int>())
                .Where(kv => treeIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        public IReadOnlyDictionary<int, TalentEffectRow> ReadTalentEffects(IReadOnlyCollection<int> talentIds)
        {
            Calls++;
            TalentIdsAsked.AddRange(talentIds);
            return (Tables?.Talents ?? new Dictionary<int, TalentEffectRow>())
                .Where(kv => talentIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }

    private static int LoadAll(SpecRootBuffMap map, ISpecRootBuffSource src)
    {
        int steps = 0;
        while (!map.LoadStep(src)) { if (++steps > 10) throw new InvalidOperationException("LoadStep never finished"); }
        return steps + 1;
    }

    [Fact]
    public void Map_BeforeLoad_UsesFallback()
    {
        var map = new SpecRootBuffMap(new StubLog());
        Assert.True(map.TryGetSpec(2202110, out var spec));
        Assert.Equal(50001, spec);
        Assert.False(map.TryGetSpec(9001, out _));
    }

    [Fact]
    public void Map_Load18FromTables_SourceTables_LogsOnce_OneTablePerStep()
    {
        var log = new StubLog();
        var map = new SpecRootBuffMap(log);
        var moved = Expected.ToDictionary(kv => kv.Key + 1, kv => kv.Value);   // a "patched" table set
        var src = new FakeSource { Tables = FakeTables(moved) };

        Assert.Equal(3, LoadAll(map, src));   // stages, trees, effects — spread over three ticks
        Assert.Equal(3, src.Calls);
        Assert.True(map.LoadStep(src));        // further steps are no-ops
        Assert.Equal(3, src.Calls);

        Assert.Equal("tables", map.Source);
        Assert.True(map.TryGetSpec(2202111, out var spec));
        Assert.Equal(50001, spec);
        Assert.False(map.TryGetSpec(2202110, out _));
        Assert.Single(log.InfoLines, l => l == "[CombatSpec] spec root buffs: 18 (source=tables)");
        // Only the 18 Expertise-II roots are fetched, never the whole tree / talent table.
        Assert.Equal(18, src.TreeIdsAsked.Count);
        Assert.Equal(18, src.TalentIdsAsked.Count);
    }

    [Fact]
    public void Map_DerivationNot18_FallsBack()
    {
        var log = new StubLog();
        var map = new SpecRootBuffMap(log);
        LoadAll(map, new FakeSource { Tables = FakeTables(new Dictionary<int, int> { { 111, 50001 } }) });

        Assert.Equal("fallback", map.Source);
        Assert.True(map.TryGetSpec(2202110, out _));
        Assert.False(map.TryGetSpec(111, out _));
        Assert.Single(log.InfoLines, l => l == "[CombatSpec] spec root buffs: 18 (source=fallback)");
    }

    [Fact]
    public void Map_TablesUnavailable_FallsBack()
    {
        var log = new StubLog();
        var map = new SpecRootBuffMap(log);
        LoadAll(map, new FakeSource());
        Assert.Equal("fallback", map.Source);
        Assert.Contains("[CombatSpec] spec root buffs: 18 (source=fallback)", log.InfoLines);
    }

    [Fact]
    public void Map_SourceThrows_NeverThrows_FallsBack()
    {
        var log = new StubLog();
        var map = new SpecRootBuffMap(log);
        LoadAll(map, new FakeSource { Throw = new InvalidOperationException("boom") });
        Assert.Equal("fallback", map.Source);
        Assert.True(map.TryGetSpec(2207180, out var spec));
        Assert.Equal(130002, spec);
    }

    [Fact]
    public void Derive_SameBuffSameSpecTwice_IsKept()
    {
        // n2: a duplicated stage row naming the SAME spec is not a collision.
        var t = FakeTables(new Dictionary<int, int> { { 111, 50001 }, { 222, 50002 } });
        var stages = new Dictionary<int, TalentStageRow>(t.Stages);
        var root = stages.Values.First(s => s.TalentStage == 1 && s.WeaponType == 5 && s.BdType == 0);
        stages[99_999] = root;
        var derived = SpecRootBuffs.Derive(t with { Stages = stages });
        Assert.Equal(50001, derived[111]);
        Assert.Equal(2, derived.Count);
    }

    [Fact]
    public void Derive_DuplicateBuffAcrossSpecs_IsRejected()
    {
        // Two specs claiming the same root buff would make the map ambiguous — derivation must not
        // silently pick one; it drops the collision so the count check falls back.
        var t = FakeTables(new Dictionary<int, int> { { 111, 50001 }, { 222, 50002 } });
        var talents = new Dictionary<int, TalentEffectRow>(t.Talents);
        foreach (var k in talents.Keys.ToList())
            if (talents[k].Effects.Any(e => e.Length >= 2 && e[0] == 3 && e[1] == 222))
                talents[k] = new TalentEffectRow(new[] { new[] { 3, 111, 1 } });
        var derived = SpecRootBuffs.Derive(t with { Talents = talents });
        Assert.DoesNotContain(111, derived.Keys);
    }
}
