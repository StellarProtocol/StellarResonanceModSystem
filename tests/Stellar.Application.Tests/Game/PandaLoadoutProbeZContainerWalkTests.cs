using System.Reflection;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED — the live-state Lua chunks must walk <b>zcontainer</b> maps by KEY and index per key,
/// never by the value <c>pairs</c> yields.
///
/// <para><b>The bug these pin.</b> Owner run <c>sea/zJr9W0iA53</c>: a ring "Replace", a module
/// "Replace" and a second ring "Replace" all took effect in-game and none was captured. "Replace"
/// is not a distinct wire path — it calls the SAME <c>CheckPutOnEquip</c> / <c>AsyncEquipMod</c> as
/// a plain equip (<c>lua/ui/item_btns/replace_equip_btn.lua:67</c> vs <c>puton_equip_btn.lua:64</c>),
/// <c>PutOnEquip</c>'s reply is a bare error code (<c>lua/zproxy/world_proxy.lua:2165-2214</c>), and
/// <c>equipList</c> has NO write path but the container-sync merge. The merge event fired; the READ
/// was blind. Every zcontainer map installs a metatable whose <c>__pairs</c> iterator hardcodes
/// <c>local v = nil</c> (<c>lua/zcontainer/equip_list.lua:218-239</c>, applied to the map itself at
/// <c>:490</c>; <c>lua/zcontainer/mod.lua:266</c>), so <c>for k,v in pairs(m)</c> yields every key
/// with a NIL value — silently. The equip walk's <c>if info~=nil</c> guard then skipped every slot
/// and the mod walk stringified nil, so BOTH live maps parsed empty on every read.
///
/// <para>A regression here is invisible at runtime (no error, no log, just an empty row that reads
/// as "the player changed nothing"), which is exactly how it survived from 2026-08-05 to
/// 2026-08-23 — so it is pinned on the chunk TEXT, the only testable surface for a Lua const.</para>
/// </summary>
public sealed class PandaLoadoutProbeZContainerWalkTests
{
    private static string Chunk(string name)
        => (string)typeof(PandaLoadoutProbe)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    // ── Chunk shape: both read paths, one shared walk ─────────────────────────────────────────

    [Theory]
    [InlineData("LiveStateChunk")]   // the merge-event re-read (PandaLoadoutProbe.LiveState.cs)
    [InlineData("RefreshChunk")]     // the on-demand refresh dump (PandaLoadoutProbe.Resolution.cs)
    public void BothChunksUseTheSharedZContainerSafeWalks(string chunkName)
    {
        var chunk = Chunk(chunkName);

        Assert.Contains(PandaLoadoutProbe.LiveEquipWalkFragment, chunk);
        Assert.Contains(PandaLoadoutProbe.LiveModWalkFragment, chunk);
    }

    [Theory]
    [InlineData("LiveStateChunk")]
    [InlineData("RefreshChunk")]
    public void NeitherChunkReadsTheValueYieldedByPairsOverAZContainerMap(string chunkName)
    {
        var chunk = Chunk(chunkName);

        // The exact broken forms that shipped. `pairs` over a zcontainer map yields (key, nil), so
        // binding a value variable at all is the defect — re-introducing either silently empties the
        // LIVE row again.
        Assert.DoesNotContain("for s,info in pairs(el)", chunk);
        Assert.DoesNotContain("for s,u in pairs(ms)", chunk);
    }

    [Fact]
    public void TheSharedWalksIndexPerKeyRatherThanBindingTheIteratedValue()
    {
        Assert.Contains("for s in pairs(el)", PandaLoadoutProbe.LiveEquipWalkFragment);
        Assert.Contains("local info=el[s]", PandaLoadoutProbe.LiveEquipWalkFragment);

        Assert.Contains("for s in pairs(ms)", PandaLoadoutProbe.LiveModWalkFragment);
        Assert.Contains("local u=ms[s]", PandaLoadoutProbe.LiveModWalkFragment);

        // Defence in depth: a nil value must never reach tostring() again. The old mod walk had no
        // guard and emitted unparseable "slot:nil" garbage instead of an honest empty read.
        Assert.Contains("if u~=nil then", PandaLoadoutProbe.LiveModWalkFragment);
    }

    /// <summary>The fix must NOT be over-applied. <c>pd.equipInfoMap</c> / <c>pd.modInfoMap</c> come
    /// from <c>weapon_data.rolePlanServerData_</c> — PLAIN tables, not zcontainers — and the game
    /// itself value-iterates them (<c>EquipVM.IsEquipByOtherPlan</c>,
    /// <c>lua/ui/view_model/equip/equip_vm.lua:311</c>). Rewriting them to the key-index form would
    /// be a pointless behaviour change on a path that already works.</summary>
    [Fact]
    public void RolePlanMapsKeepTheValueIteratingFormBecauseTheyAreNotZContainers()
    {
        var chunk = Chunk("RefreshChunk");

        Assert.Contains("for s,u in pairs(pd.equipInfoMap)", chunk);
        Assert.Contains("for s,u in pairs(pd.modInfoMap)", chunk);
    }

    // ── What the broken walks actually produced, and what the fixed ones do ───────────────────

    /// <summary>The measured symptom, pinned as a parser fact: the rows the BROKEN walks emitted
    /// parse to EMPTY maps. An empty live equip AND mod map drives <c>PerClassResolve</c>'s
    /// <c>hasLive</c> false, so the live overlay is skipped entirely and the served gear falls back
    /// to the cooldown-refreshed saved plan — the pre-Replace ring the owner saw at archive time.</summary>
    [Fact]
    public void TheBrokenWalksOutputParsedToEmptyMaps()
    {
        // equipList: every slot skipped by the `info~=nil` guard → an empty column.
        // modSlots: nil stringified per slot → "slot:nil", which ParseUuidMap drops entirely.
        var live = PandaLoadoutProbe.ParseLiveLine("LIVE\t\t1:nil,2:nil,3:nil,4:nil\t4\t106\t69126");

        Assert.Empty(live.Equip);
        Assert.Empty(live.Mod);
    }

    [Fact]
    public void TheFixedWalksOutputParsesToPopulatedMaps()
    {
        var live = PandaLoadoutProbe.ParseLiveLine("LIVE\t200:2000835,201:2010937\t1:5500103,2:5500207\t4\t106\t69126");

        Assert.Equal(2, live.Equip.Count);
        Assert.Equal(2000835L, live.Equip[200]);
        Assert.Equal(2, live.Mod.Count);
        Assert.Equal(5500103L, live.Mod[1]);
    }

    /// <summary>End-to-end change gate for the owner's two Replace shapes: with the walks fixed, a
    /// ring swapped in place and a module swapped in place each read as a real difference, which is
    /// what raises <c>LiveStateChanged</c> and re-captures the setup. Under the broken walks BOTH
    /// sides were empty and therefore identical — no event, no capture.</summary>
    [Fact]
    public void ReplacingARingOrAModuleInPlaceIsSeenAsAChange()
    {
        var before = PandaLoadoutProbe.ParseLiveLine("LIVE\t200:2000835,201:2010937\t1:5500103\t4\t106\t69126");
        var afterRingReplace = PandaLoadoutProbe.ParseLiveLine("LIVE\t200:2071330,201:2010937\t1:5500103\t4\t106\t69126");
        var afterModReplace = PandaLoadoutProbe.ParseLiveLine("LIVE\t200:2000835,201:2010937\t1:5500911\t4\t106\t69126");

        Assert.True(PandaLoadoutProbe.LiveStateDiffers(before, afterRingReplace));
        Assert.True(PandaLoadoutProbe.LiveStateDiffers(before, afterModReplace));

        // And the pre-fix reality: two empty reads are indistinguishable, so nothing ever fired.
        var brokenA = PandaLoadoutProbe.ParseLiveLine("LIVE\t\t1:nil\t4\t106\t69126");
        var brokenB = PandaLoadoutProbe.ParseLiveLine("LIVE\t\t1:nil\t4\t106\t69126");
        Assert.False(PandaLoadoutProbe.LiveStateDiffers(brokenA, brokenB));
    }
}
