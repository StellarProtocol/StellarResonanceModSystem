using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Weapon-skin half of <see cref="PandaFashionProbe"/>. The game keeps weapon skins OUTSIDE the outfit
/// map — PER CLASS, in <c>CharSerialize.professionList.professionList[pid].UseSkinId</c> (what the
/// Wardrobe's Weapon Skin tab shows for "(Current)": <c>weapon_skill_skin_vm.GetWeaponSkinId</c>) — and
/// switches them with a separate RPC, <c>WorldProxy.UseProfessionSkin({professionId, skinId})</c>
/// (<c>weapon_skill_skin_vm.AsyncUseProfessionSkin</c>, driven by the tab's Save button). Capture rides
/// the outfit capture chunk; apply reuses the outfit apply's pending slot + result global.
/// RE: <c>docs/recon/wardrobe-fashion-preview.md</c> § Weapon skin.
/// </summary>
internal sealed partial class PandaFashionProbe
{
    private const string WeaponGlobal = "_StellarWardrobeWeapon";

    // Current class's worn weapon skin, written on the Update tick with the outfit snapshot. Null until the
    // first in-world capture parses.
    private WardrobeWeaponSkin? _weaponSkin;

    public WardrobeWeaponSkin? ReadWornWeaponSkin() => _weaponSkin;

    public Task<int> CallApplyWeaponSkinAsync(int professionId, int skinId, CancellationToken ct)
        => Dispatch(BuildWeaponSkinChunk(professionId, skinId),
            () => string.Format(CultureInfo.InvariantCulture, "weapon skin class={0} skin={1}", professionId, skinId), ct);

    // Read back the weapon global the capture chunk wrote. An unparseable / empty value means "not ready"
    // — keep the last snapshot rather than blanking it (same rule as the outfit map).
    private void CaptureWeaponSkin()
    {
        var parsed = ParseWeaponSkin(ReadLuaGlobalString(WeaponGlobal));
        if (parsed is null) return;
        _weaponSkin = parsed;
        DiagWeaponCaptured(parsed);
    }

    // Weapon-skin read, spliced into CaptureChunk (runs inside its coroutine, after `cs` is bound). Emits
    // "<professionId>:<skinId>" into WeaponGlobal (skin 0 = the class's default look), or "" when the
    // profession container isn't ready. pcall-guarded so a missing profession list can never break the
    // outfit capture that precedes it. No interpolation — no injection surface.
    private const string WeaponCaptureLua =
        " local w=\"\"" +
        " pcall(function()" +
        "  if cs~=nil and cs.professionList~=nil then" +
        "   local pl=cs.professionList local cur=pl.curProfessionId" +
        "   if cur~=nil and cur~=0 then" +
        "    local p=(pl.professionList)[cur] local sk=0" +
        "    if p~=nil and p.UseSkinId~=nil then sk=p.UseSkinId end" +
        "    w=tostring(cur)..\":\"..tostring(sk)" +
        "   end" +
        "  end" +
        " end)" +
        " rawset(_G,\"" + WeaponGlobal + "\", w)";

    /// <summary>Parse the capture global <c>"&lt;professionId&gt;:&lt;skinId&gt;"</c> (Lua <c>tostring</c> of two
    /// ints; a float-formatted <c>"5.0"</c> is tolerated). Null for empty / malformed input = not ready.</summary>
    internal static WardrobeWeaponSkin? ParseWeaponSkin(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var sep = raw!.IndexOf(':');
        if (sep <= 0 || sep == raw.Length - 1) return null;
        if (!TryParseLuaInt(raw.Substring(0, sep), out var professionId)) return null;
        if (!TryParseLuaInt(raw.Substring(sep + 1), out var skinId)) return null;
        if (professionId <= 0 || skinId < 0) return null;
        return new WardrobeWeaponSkin(professionId, skinId);
    }

    private static bool TryParseLuaInt(string s, out int value)
    {
        var dot = s.IndexOf('.');
        if (dot >= 0) s = s.Substring(0, dot);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    // Apply chunk — mirrors weapon_skill_skin_vm.AsyncUseProfessionSkin step for step: skin 0 → the class's
    // origin (default) skin via the VM's own GetWeaponOriginSkinId; WorldProxy.UseProfessionSkin({professionId,
    // skinId}, token); on ok, the VM's OnWeaponSkinChange dispatch so the local render + UI refresh. The bare
    // game code lands in ApplyGlobal (0 = ok; the proxy wrapper raises on transport errors, which the xpcall
    // wrapper logs and the C# side times out as -1). Ints we control, InvariantCulture — no injection surface.
    internal static string BuildWeaponSkinChunk(int professionId, int skinId)
        => string.Format(
            CultureInfo.InvariantCulture,
            "(Z.CoroUtil.create_coro_xpcall(function()" +
            " local pid={0} local skin={1}" +
            " local vm=Z.VMMgr.GetVM(\"weapon_skill_skin\")" +
            " if skin==0 and vm~=nil then local ok,origin=pcall(function() return vm:GetWeaponOriginSkinId(pid) end) if ok and origin~=nil then skin=origin end end" +
            " local wp=require(\"zproxy.world_proxy\")" +
            " local ret=(wp.UseProfessionSkin)({{professionId = pid, skinId = skin}}, {2})" +
            " if ret==nil then ret=0 end" +
            " if ret==0 then pcall(function() (Z.EventMgr):Dispatch(((Z.ConstValue).Weapon).OnWeaponSkinChange) end) end" +
            " rawset(_G,\"{3}\", tostring(ret))" +
            " end))()",
            professionId, skinId, NeverCancelToken, ApplyGlobal);
}
