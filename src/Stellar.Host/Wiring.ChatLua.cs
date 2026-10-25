using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Lua-polled chat sources (Wiring.ChatLua.cs) — data the wire capture can't see ──
    private SystemNoticeService? _systemNotices;
    private FriendsService? _friendsService;   // IPluginServices.Friends

    /// <summary>
    /// Builds the Lua-polled chat services and subscribes them to the framework <c>Update</c> tick, which only fires
    /// while <c>IsWorldActive</c> (they touch the game's chat VM). Each self-throttles and is LAZY: it does no Lua work
    /// until a plugin uses it (system notices: a chat consumer; friends: a subscriber / action / list read), so a setup
    /// with no chat plugin pays one cheap check per tick. Runs in <c>Load()</c> after
    /// <see cref="BuildCoreServices"/> (needs <c>_luaService</c>, <c>_chatService</c>, <c>_combatService</c>).
    /// </summary>
    private void WireChatLuaServices(BepInExPluginLog log)
    {
        // Client-generated system notices ("Obtained …") → IChat.MessageReceived on ChatChannel.System. Stamped with
        // the framework's interpolated server clock (same domain as wire chat timestamps), UtcNow before first sync.
        _systemNotices = new SystemNoticeService(_luaService!, _chatService!, () => _combatService?.ServerNowMs ?? 0L, log.Info);
        _framework!.Update += dt => _systemNotices!.Tick(dt);
        // Friend list + private-chat conversations (IFriends): Lua-polled every 2.5 s in-world, rebuilt only on change;
        // queued actions run the game's own coroutine VM calls on the next tick.
        _friendsService = new FriendsService(_luaService!, log.Info);
        _framework!.Update += dt => _friendsService!.Tick(dt);
    }
}
