using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Wardrobe (fashion) services (Wiring.Wardrobe.cs) ─────────────────────
    private PandaFashionProbe? _fashionProbe;
    private WardrobeService? _wardrobeService;

    /// <summary>
    /// Constructs the wardrobe capture/apply probe + service. The probe reads the worn cosmetic
    /// outfit from <c>CharSerialize.fashion.wearInfo</c> and applies a saved outfit through the game's
    /// own <c>WorldProxy.FashionWear</c> RPC over the tolua# LuaState bridge (same shape as the loadout
    /// probe). Built after the loadout/inventory services (it wires the container-merge event to
    /// re-capture the worn outfit). Ticked world-gated from the Host service tick.
    /// </summary>
    private void BuildWardrobeServices(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        _fashionProbe = new PandaFashionProbe(log, typeRegistry);
        // A fashion change funnels through the same field-agnostic CharSerialize container merge as a
        // gear/module/imagine change, so SelfGearChanged also covers an outfit edit — re-capture the worn
        // set when it fires. The handler only flips a flag (raised on the network thread).
        _inventoryService!.SelfGearChanged += _fashionProbe.OnGearChanged;
        _wardrobeService = new WardrobeService(_fashionProbe);
    }
}
