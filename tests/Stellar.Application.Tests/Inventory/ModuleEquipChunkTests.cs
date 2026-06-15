using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Inventory;

/// <summary>
/// Locks the Lua chunk <see cref="PandaModuleEquipProbe"/> runs through
/// <c>LuaState.DoString</c>. The chunk must mirror the game's own equip buttons
/// (<c>ui/item_btns/mod_install_btn.lua</c>): fetch the module-local ViewModel
/// via <c>Z.VMMgr.GetVM("mod")</c> — its only reachable accessor — then call the
/// async function positionally, all launched inside the game's canonical
/// <c>Z.CoroUtil.create_coro_xpcall(fn)()</c> coroutine wrapper (the equip RPC
/// yields, so it must run off the main thread). A regression here (missing
/// coroutine wrap, wrong accessor, <c>:</c> method-call self, mangled args)
/// either throws "yield from outside a coroutine" or silently no-ops in-world.
/// </summary>
public sealed class ModuleEquipChunkTests
{
    [Fact]
    public void BuildEquipChunk_MirrorsTheGameButtonCall_InsideCoroutine()
    {
        var chunk = PandaModuleEquipProbe.BuildEquipChunk("mod", "AsyncEquipMod", 330L, 1);

        Assert.Equal(
            "(Z.CoroUtil.create_coro_xpcall(function() local vm=Z.VMMgr.GetVM(\"mod\"); vm.AsyncEquipMod(330, 1, ZUtil.ZCancelSource.NeverCancelToken) end))()",
            chunk);
    }

    [Fact]
    public void BuildUninstallChunk_PassesSlotAndToken_InsideCoroutine()
    {
        var chunk = PandaModuleEquipProbe.BuildUninstallChunk("mod", "AsyncUninstallMod", 2);

        Assert.Equal(
            "(Z.CoroUtil.create_coro_xpcall(function() local vm=Z.VMMgr.GetVM(\"mod\"); vm.AsyncUninstallMod(2, ZUtil.ZCancelSource.NeverCancelToken) end))()",
            chunk);
    }

    [Fact]
    public void BuildEquipChunk_RendersLargeUuidAsPlainIntegerLiteral()
    {
        // Item uuids are int64; the literal must stay a bare integer (no grouping
        // separators, no float/exponent) so Lua 5.3 parses it as an integer for
        // the int64 mod_uuid proto field.
        const long uuid = 4503599627370497L;

        var chunk = PandaModuleEquipProbe.BuildEquipChunk("mod", "AsyncEquipMod", uuid, 4);

        Assert.Equal(
            "(Z.CoroUtil.create_coro_xpcall(function() local vm=Z.VMMgr.GetVM(\"mod\"); vm.AsyncEquipMod(4503599627370497, 4, ZUtil.ZCancelSource.NeverCancelToken) end))()",
            chunk);
    }
}
