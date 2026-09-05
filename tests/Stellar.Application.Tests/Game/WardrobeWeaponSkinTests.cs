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

    [Theory]
    [InlineData("5:160", 5, 160)]
    [InlineData("5:0", 5, 0)]           // class wears its default weapon look
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
