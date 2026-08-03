namespace Stellar.Abstractions.Services;

/// <summary>
/// Bridge to the game's live tolua# Lua state (<c>LuaInterface.LuaState.mainState</c>) where all
/// <c>Z.*</c> game globals are registered. Lets a plugin run a Lua chunk and read simple global values
/// back, without hand-rolling the reflection resolution in every plugin.
/// <para><b>Main-thread only, fire-and-forget.</b> Every member touches the game's Lua stack, which is not
/// thread-safe and must be driven from the Unity main thread — call from inside
/// <see cref="IFramework.Update"/> or <see cref="IFrameworkTiming.Post"/>, never from a chat/network
/// callback thread. There is no way to return a value from a chunk and <b>no C# callback may be passed into
/// Lua</b> (a native Lua→C# callback crashes the IL2CPP runtime); park a result in a global with
/// <c>rawset(_G, key, value)</c> and read it via the typed getters below.</para>
/// </summary>
public interface ILua
{
    /// <summary>True once <c>LuaState.mainState</c> and the <c>DoString</c> entry point have resolved
    /// (post-login). Chunks issued before this is true are dropped, so gate on it or simply retry each tick.</summary>
    bool Ready { get; }

    /// <summary>Runs <paramref name="chunk"/> in the game's main Lua state on the calling (main) thread.
    /// Fire-and-forget: no return value. Wrap the chunk body in <c>pcall(function() ... end)</c> so a Lua-side
    /// error cannot propagate back through the interop trampoline. No-op until <see cref="Ready"/>.</summary>
    /// <param name="chunk">A complete Lua chunk.</param>
    void DoString(string chunk);

    /// <summary>Reads global <paramref name="key"/> as a Lua boolean via the tolua# stack API. Returns false
    /// (with <paramref name="value"/> = false) when the global is absent, nil, or not a boolean.</summary>
    /// <param name="key">Global name (as written with <c>rawset(_G, key, value)</c>).</param>
    /// <param name="value">The decoded boolean, or false on failure.</param>
    bool TryReadGlobalBool(string key, out bool value);

    /// <summary>Reads global <paramref name="key"/> as a managed string via the tolua# stack API
    /// (<c>LuaGetGlobal</c>→<c>LuaToString</c>). Returns null when the global is absent or not string-convertible.</summary>
    /// <param name="key">Global name.</param>
    string? ReadGlobalString(string key);

    /// <summary>Reads global <paramref name="key"/> as a Lua number via the tolua# stack API. Returns false
    /// (with <paramref name="value"/> = 0) when the global is absent, nil, or not a number.</summary>
    /// <param name="key">Global name.</param>
    /// <param name="value">The decoded number, or 0 on failure.</param>
    bool TryReadGlobalNumber(string key, out double value);
}
