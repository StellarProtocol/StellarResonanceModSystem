using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Locks the weapon-skin half of <see cref="PandaFashionProbe"/> (framework 2.6.0, Discord feature request
/// "save weapon along with the outfit template", 2026-09-03). The game keeps weapon skins PER CLASS outside
/// the outfit map and switches them with <c>WorldProxy.UseProfessionSkin</c>; the chunks here must mirror
/// <c>weapon_skill_skin_vm.lua</c> (<c>GetWeaponSkinId</c> for the read, <c>AsyncUseProfessionSkin</c> for
/// the apply: skin 0 → the class's origin skin, <c>OnWeaponSkinChange</c> dispatch on ok).
/// Framework 2.6.1 adds the capture-side half of that same 0 ⇄ origin contract — see
/// <see cref="CaptureChunk_ReportsTheCurrentWeaponsOriginSkin_AsNoSkin"/>.
/// </summary>
public sealed class WardrobeWeaponSkinTests
{
    [Fact]
    public void ApplyChunk_MirrorsAsyncUseProfessionSkin_InsideCoroutine()
    {
        var chunk = PandaFashionProbe.BuildWeaponSkinChunk(5, 160);

        Assert.Equal(
            "(Z.CoroUtil.create_coro_xpcall(function()" +
            " local pid=5 local skin=160" +
            " local vm=Z.VMMgr.GetVM(\"weapon_skill_skin\")" +
            " if skin==0 and vm~=nil then local ok,origin=pcall(function() return vm:GetWeaponOriginSkinId(pid) end) if ok and origin~=nil then skin=origin end end" +
            " local wp=require(\"zproxy.world_proxy\")" +
            " local ret=(wp.UseProfessionSkin)({professionId = pid, skinId = skin}, ZUtil.ZCancelSource.NeverCancelToken)" +
            " if ret==nil then ret=0 end" +
            " if ret==0 then pcall(function() (Z.EventMgr):Dispatch(((Z.ConstValue).Weapon).OnWeaponSkinChange) end) end" +
            " rawset(_G,\"_StellarWardrobeApply\", tostring(ret))" +
            " end))()",
            chunk);
    }

    [Fact]
    public void CaptureChunk_ReadsTheCurrentClassSkin_IntoItsOwnGlobal_AfterTheOutfit()
    {
        var chunk = PandaFashionProbe.CaptureChunk;

        // The game's own read: CharSerialize.professionList.professionList[curProfessionId].UseSkinId.
        Assert.Contains("local pl=cs.professionList local cur=pl.curProfessionId", chunk);
        Assert.Contains("local p=(pl.professionList)[cur]", chunk);
        Assert.Contains("if p~=nil and p.UseSkinId~=nil then sk=p.UseSkinId end", chunk);
        Assert.Contains("rawset(_G,\"_StellarWardrobeWeapon\", w)", chunk);
        // The outfit global is written BEFORE the weapon read, so a weapon-side failure can't lose the outfit.
        Assert.True(chunk.IndexOf("_StellarWardrobeWorn", System.StringComparison.Ordinal)
                    < chunk.IndexOf("_StellarWardrobeWeapon", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression pin — owner bug 2026-09-05, after the 2.6.0 release: "no weapon skin never been saved
    /// when user select that and click store outfit." The Wardrobe's ⊘ tile is NOT skin 0: the tab marks
    /// worn the tile whose Id equals <c>GetWeaponOriginSkinId(curProfessionId)</c>
    /// (fashion_weapon_skin_select_view.lua:116, <c>isEmpty = value.Original == 1</c>), so picking it stores
    /// a concrete <c>WeaponSkinTable</c> row (measured: outfit 9 held profession 5 / skin 7350002
    /// "Mirrorlight Ring", <c>Original: 1</c>). Capture must report that as 0 — the contract both consumers
    /// already resolve back — or the outfit pins the player to the weapon they saved with.
    /// </summary>
    [Fact]
    public void CaptureChunk_ReportsTheCurrentWeaponsOriginSkin_AsNoSkin()
    {
        var chunk = PandaFashionProbe.CaptureChunk;

        // Mirrors the tab's own worn-tile test, on the container's curProfessionId (== the VM's
        // GetContainerProfession, which GetWeaponOriginSkinId requires to match or it returns nil).
        Assert.Contains(
            "    if sk~=0 then pcall(function()" +
            "     local svm=((Z.VMMgr).GetVM)(\"weapon_skill_skin\")" +
            "     local origin=svm and svm:GetWeaponOriginSkinId(cur)" +
            "     if origin~=nil and origin~=0 and sk==origin then sk=0 end" +
            "    end) end",
            chunk);

        // Its OWN pcall, nested inside the weapon block's: a failing origin lookup keeps the RAW skin id
        // rather than losing the capture (dry-run cases c/e/f: GetVM nil / GetVM throws / call throws).
        var normalise = chunk.IndexOf("local origin=svm", System.StringComparison.Ordinal);
        var innerPcall = chunk.IndexOf("if sk~=0 then pcall(function()", System.StringComparison.Ordinal);
        Assert.InRange(innerPcall, 0, normalise);

        // The normalisation runs BEFORE the global is composed, so what C# parses is already normalised.
        Assert.InRange(
            normalise, 0, chunk.IndexOf("w=tostring(cur)..\":\"..tostring(sk)", System.StringComparison.Ordinal));

        // …and still after the outfit global — a weapon-side failure can never lose the outfit.
        Assert.InRange(
            chunk.IndexOf("_StellarWardrobeWorn", System.StringComparison.Ordinal), 0, innerPcall);
    }

    [Theory]
    [InlineData("5:160", 5, 160)]
    [InlineData("5:0", 5, 0)]           // ⊘ "no skin": the class wears its current weapon's own look
    [InlineData("13:4210.0", 13, 4210)] // Lua float formatting tolerated
    public void ParseWeaponSkin_ReadsClassAndSkin(string raw, int professionId, int skinId)
    {
        Assert.Equal(new WardrobeWeaponSkin(professionId, skinId), PandaFashionProbe.ParseWeaponSkin(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("5:")]
    [InlineData(":160")]
    [InlineData("0:160")]     // no current class yet
    [InlineData("nil:nil")]
    public void ParseWeaponSkin_NotReadyShapes_ReturnNull(string? raw)
    {
        Assert.Null(PandaFashionProbe.ParseWeaponSkin(raw));
    }
}
