using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Game side of the sticky cursor. The free cursor is the game's own show-mouse request: Lua <c>UIManager.UpdateMouseVisible</c>
/// (<c>ui_manager.lua:669</c>) rewrites the <c>ZMouseManager</c> UI source on every view show/hide, so setting that source
/// from C# would be overwritten; a key in <c>showMouseView_</c> is what the game's own chat uses and survives other panels
/// opening and closing. Escape goes through the Rewired keyboard prefix (<see cref="HotkeyKeyBlockPatch"/>), the mouse
/// through <see cref="RewiredMouseGate"/>. See Knowledge Base <c>Harmony-Rewired-Input-Patching.md</c> (free-cursor section).
/// </summary>
internal sealed class StickyCursorBackend : IStickyCursorBackend
{
    // pcall: a Lua error must never propagate back through the interop trampoline (ILua contract). Z.UIMgr is checked
    // because the call is skipped silently if the UI manager isn't up rather than raising inside the pcall.
    private const string AddChunk =
        "pcall(function() if Z and Z.UIMgr then Z.UIMgr:AddShowMouseView(\"stellar_sticky_cursor\") end end)";
    private const string RemoveChunk =
        "pcall(function() if Z and Z.UIMgr then Z.UIMgr:RemoveShowMouseView(\"stellar_sticky_cursor\") end end)";

    private readonly ILua _lua;
    private readonly HotkeyKeyBlockPatch _keyBlock;
    private readonly RewiredMouseGate _mouse;

    public StickyCursorBackend(ILua lua, HotkeyKeyBlockPatch keyBlock, RewiredMouseGate mouse)
    {
        _lua = lua;
        _keyBlock = keyBlock;
        _mouse = mouse;
    }

    public bool LuaReady => _lua.Ready;

    public void RequestGameCursor(bool on) => _lua.DoString(on ? AddChunk : RemoveChunk);

    public void SetEscapeBlocked(bool on) => _keyBlock.SetEscapeSwallow(on);

    public void SetWorldMouseBlocked(bool on) => _mouse.SetBlocked(on);
}
