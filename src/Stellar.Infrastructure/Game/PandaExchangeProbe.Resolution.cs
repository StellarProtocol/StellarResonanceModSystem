using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Domain.Exchange;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Lua-bridge reflection-resolution + <b>WorldProxy</b> exchange-RPC chunk builders + reply parsers for
/// <see cref="PandaExchangeProbe"/>.
///
/// <para>Resolves the game's <b>tolua#</b> <c>LuaState</c> + <c>DoString</c> entry point identically to
/// <see cref="PandaLoadoutProbe"/> (static <c>ZLuaFramework.LuaState.mainState</c> + <c>void DoString(string,string)</c>),
/// then drives <c>require("zproxy.world_proxy").&lt;Rpc&gt;(requestTable, NeverCancelToken)</c> inside
/// <c>Z.CoroUtil.create_coro_xpcall</c>. Replies are written to a per-kind Lua global and read back via the
/// <c>LuaState</c> string indexer (decoding the IL2CPP-wrapped string). Request + reply fields are
/// <b>camelCase</b> (e.g. <c>configId</c>, <c>errCode</c>, <c>lowestPrice</c>); the buy returns a bare
/// <c>EErrorCode</c> number. See <c>recon/exchange-vm-notes.md</c> Pass 5.</para>
/// </summary>
internal sealed partial class PandaExchangeProbe
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string ChunkName = "Stellar.Exchange";

    // Per-kind reply globals (distinct so concurrent kinds never collide).
    private const string CareGlobal = "_StellarExchangeCare";
    private const string ListingsGlobal = "_StellarExchangeListings";
    private const string NoticeGlobal = "_StellarExchangeNotice";
    private const string BuyGlobal = "_StellarExchangeBuy";

    private const string Proxy = "local wp=require(\"zproxy.world_proxy\") local tok=ZUtil.ZCancelSource.NeverCancelToken";

    private volatile bool _bridgeResolved;
    private bool _resolutionFailureLogged;

    private MethodInfo? _mainStateGetter;   // static LuaState mainState { get; }
    private MethodInfo? _doString;          // void DoString(string chunk, string chunkName)
    private MethodInfo? _getItem;           // object get_Item(string global) — Lua string indexer

    private int _resolveTickCounter;
    private const int ResolveAttemptEveryTicks = 60;

    /// <summary>Proactively resolve the Lua bridge off the Update tick (throttled) so
    /// <see cref="IsResolved"/> / <c>IExchange.IsAvailable</c> flips true without a dispatch. No-op once resolved.</summary>
    internal void TryResolveBridgeIfDue()
    {
        if (_bridgeResolved) return;
        if (_resolveTickCounter++ % ResolveAttemptEveryTicks != 0) return;
        EnsureBridgeResolved();
    }

    private bool EnsureBridgeResolved()
    {
        if (_bridgeResolved) return true;
        try { return TryResolveBridge(); }
        catch (Exception ex)
        {
            OnResolutionFailure($"bridge resolution threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TryResolveBridge()
    {
        var luaStateType = _typeRegistry.FindType("ZLuaFramework.LuaState")
            ?? _typeRegistry.FindType("LuaInterface.LuaState")
            ?? FindTypeByShortName("LuaState");
        if (luaStateType is null)
        {
            OnResolutionFailure("ZLuaFramework.LuaState type not loaded yet");
            return false;
        }

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        if (_mainStateGetter is null)
        {
            OnResolutionFailure("LuaState.mainState (static property) not found");
            return false;
        }

        _doString = FindDoString(luaStateType);
        if (_doString is null)
        {
            OnResolutionFailure("LuaState.DoString(string,string) not found");
            return false;
        }

        _getItem = luaStateType.GetMethod("get_Item", AnyInstance, binder: null,
            types: new[] { typeof(string) }, modifiers: null);

        _bridgeResolved = true;
        OnResolutionSucceeded();
        return true;
    }

    private static MethodInfo? FindDoString(Type luaStateType)
    {
        foreach (var m in luaStateType.GetMethods(AnyInstance))
        {
            if (m.Name != "DoString" || m.IsGenericMethodDefinition) continue;
            if (m.ReturnType != typeof(void)) continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
            {
                return m;
            }
        }
        return null;
    }

    private object? GetMainLuaState()
    {
        if (_mainStateGetter is null) return null;
        try { return _mainStateGetter.Invoke(null, Array.Empty<object>()); }
        catch { return null; }
    }

    // Runs a chunk via DoString. Returns false on a marshalling failure; a Lua-side error is reported by the
    // game's own xpcall handler under ChunkName, not thrown as a C# exception.
    private bool InvokeChunk(string chunk)
    {
        var state = GetMainLuaState();
        if (state is null)
        {
            OnResolutionFailure("LuaState.mainState returned null at dispatch");
            return false;
        }
        if (_doString is null) return false;

        try
        {
            _doString.Invoke(state, new object[] { chunk, ChunkName });
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][Exchange] Lua dispatch threw: {inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }

    // Reads one Lua string global via the tolua# LuaState string indexer, decoding the IL2CPP-wrapped result.
    // Returns null if the bridge / indexer is unresolved or the global is unset.
    private string? ReadLuaGlobalString(string globalName)
    {
        var state = GetMainLuaState();
        if (state is null || _getItem is null) return null;
        try
        {
            var text = CoerceLuaString(_getItem.Invoke(state, new object[] { globalName }));
            return string.Equals(text, "Il2CppSystem.Object", StringComparison.Ordinal) ? null : text;
        }
        catch { return null; }
    }

    // The tolua# indexer returns the Lua string boxed as an Il2CppSystem.Object whose managed ToString()
    // yields the wrapper type name, not the content. Decode the underlying IL2CPP string.
    private static string? CoerceLuaString(object? val)
    {
        if (val is null) return null;
        if (val is string s) return s;
        if (val is Il2CppObjectBase ob)
        {
            try
            {
                var ptr = ob.Pointer;
                if (ptr != IntPtr.Zero) return IL2CPP.Il2CppStringToManaged(ptr);
            }
            catch { /* not an IL2CPP string — fall through */ }
        }
        return val.ToString();
    }

    // ── Chunk builders (Approach A — worldProxy.<Rpc>) ───────────────────────────
    // Each runs inside create_coro_xpcall, writes a result string to the kind's global. CONFIRMED in-game
    // (smoke 2026-06-24): the reply `items` is a 1-BASED Lua sequence table (NOT an IL2CPP List — `.Count`
    // is nil, `[0]` is nil, `[1]` is the first element) → iterate with `ipairs`. Fields are camelCase
    // (care: configId/num; listings: price/guid; notice: price/noticeTime). errCode 0 = success.

    private static string ClearGlobalChunk(string global) => "rawset(_G,\"" + global + "\", nil)";

    // ExchangeCareList({type}) -> { items:[ExchangeItemInfo{configId,num,minPrice,isCare}], errCode }.
    private static string BuildCareChunk(int typeArg) =>
        "(Z.CoroUtil.create_coro_xpcall(function() " + Proxy +
        " local r=wp.ExchangeCareList({type=" + Int(typeArg) + "}, tok)" +
        " if r==nil then rawset(_G,\"" + CareGlobal + "\",\"ERR:nil\") return end" +
        " local ec=r.errCode or 0 if ec~=0 then rawset(_G,\"" + CareGlobal + "\",\"ERR:\"..tostring(ec)) return end" +
        " local t={\"OK\"} local items=r.items" +
        " if items~=nil then for _,it in ipairs(items) do" +
        "  t[#t+1]=string.format(\"%d\\t%d\", it.configId or 0, it.num or 0) end end" +
        " rawset(_G,\"" + CareGlobal + "\", table.concat(t,\"\\n\")) end))()";

    // GetExchangeItem({configId,page,filter}) -> { items:[ExchangePriceItemData{price,num,itemInfo,guid}], errCode }.
    private static string BuildListingsChunk(int itemId) =>
        "(Z.CoroUtil.create_coro_xpcall(function() " + Proxy +
        " local r=wp.GetExchangeItem({configId=" + Int(itemId) + ", page=0, filter={}}, tok)" +
        " if r==nil then rawset(_G,\"" + ListingsGlobal + "\",\"ERR:nil\") return end" +
        " local ec=r.errCode or 0 if ec~=0 then rawset(_G,\"" + ListingsGlobal + "\",\"ERR:\"..tostring(ec)) return end" +
        " local t={\"OK\"} local items=r.items" +
        " if items~=nil then for _,it in ipairs(items) do" +
        "  t[#t+1]=string.format(\"%d\", it.price or 0)..\"\\t\"..tostring(it.guid or \"\") end end" +
        " rawset(_G,\"" + ListingsGlobal + "\", table.concat(t,\"\\n\")) end))()";

    // ExchangeNoticeDetail({configId,page,filter}) -> { items:[ExchangePriceItemData{price,noticeTime}], errCode }.
    private static string BuildNoticeChunk(int itemId) =>
        "(Z.CoroUtil.create_coro_xpcall(function() " + Proxy +
        " local r=wp.ExchangeNoticeDetail({configId=" + Int(itemId) + ", page=0, filter={}}, tok)" +
        " if r==nil then rawset(_G,\"" + NoticeGlobal + "\",\"ERR:nil\") return end" +
        " local ec=r.errCode or 0 if ec~=0 then rawset(_G,\"" + NoticeGlobal + "\",\"ERR:\"..tostring(ec)) return end" +
        " local t={\"OK\"} local items=r.items" +
        " if items~=nil then for _,it in ipairs(items) do" +
        "  t[#t+1]=string.format(\"%d\", it.price or 0)..\"\\t\"..string.format(\"%d\", it.noticeTime or 0) end end" +
        " rawset(_G,\"" + NoticeGlobal + "\", table.concat(t,\"\\n\")) end))()";

    // ExchangeBuyItem({configId,num,price,uuid}) -> bare EErrorCode number (0 = success).
    private static string BuildBuyChunk(int itemId, int qty, long price) =>
        "(Z.CoroUtil.create_coro_xpcall(function() " + Proxy +
        " local ret=wp.ExchangeBuyItem({configId=" + Int(itemId) + ", num=" + Int(qty) +
        ", price=" + price.ToString(CultureInfo.InvariantCulture) + ", uuid=\"\"}, tok)" +
        " rawset(_G,\"" + BuyGlobal + "\", \"BUY:\"..tostring(ret)) end))()";

    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);

    // ── Reply parsers ────────────────────────────────────────────────────────────
    // Reply format: first line "OK" or "ERR:<code>"; subsequent lines are tab-separated records.

    private IReadOnlyList<ExchangeCareItem> ParseCare(string reply)
    {
        if (!StartsOk(reply, out var lines)) return NoCare;
        var list = new List<ExchangeCareItem>(lines.Length);
        for (var i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split('\t');
            if (f.Length >= 2 && TryInt(f[0], out var id) && TryInt(f[1], out var num))
                list.Add(new ExchangeCareItem(id, num));
        }
        return list;
    }

    private IReadOnlyList<ExchangeListing> ParseListings(string reply, int itemId)
    {
        if (!StartsOk(reply, out var lines)) return NoListings;
        var list = new List<ExchangeListing>(lines.Length);
        for (var i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split('\t');
            if (f.Length >= 1 && TryLong(f[0], out var price))
                list.Add(new ExchangeListing(itemId, price, f.Length >= 2 ? f[1] : string.Empty));
        }
        list.Sort(static (a, b) => a.Price.CompareTo(b.Price));   // cheapest-first
        return list;
    }

    private IReadOnlyList<ExchangeNoticeListing> ParseNotice(string reply, int itemId)
    {
        if (!StartsOk(reply, out var lines)) return NoNotice;
        var list = new List<ExchangeNoticeListing>(lines.Length);
        for (var i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split('\t');
            if (f.Length >= 2 && TryLong(f[0], out var price) && TryLong(f[1], out var unix))
                list.Add(new ExchangeNoticeListing(itemId, price, DateTimeOffset.FromUnixTimeSeconds(unix)));
        }
        return list;
    }

    private ExchangeBuyRaw ParseBuy(string reply)
    {
        // "BUY:<code>" — bare EErrorCode (0 = success).
        const string prefix = "BUY:";
        if (reply.StartsWith(prefix, StringComparison.Ordinal) && TryInt(reply.AsSpan(prefix.Length), out var code))
            return new ExchangeBuyRaw(code == 0, code, false);
        return new ExchangeBuyRaw(false, null, false);   // unparseable → rejected, not a timeout
    }

    private static bool StartsOk(string reply, out string[] lines)
    {
        lines = reply.Split('\n');
        return lines.Length > 0 && string.Equals(lines[0], "OK", StringComparison.Ordinal);
    }

    private static bool TryInt(ReadOnlySpan<char> s, out int v) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    private static bool TryInt(string s, out int v) => TryInt(s.AsSpan(), out v);

    private static bool TryLong(string s, out long v) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    private static Type? FindTypeByShortName(string shortName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName;
            try { asmName = asm.GetName().Name ?? string.Empty; }
            catch { continue; }
            if (ShouldSkipAssemblyForScan(asmName)) continue;

            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t is null) continue;
                string name;
                try { name = t.Name; } catch { continue; }
                if (string.Equals(name, shortName, StringComparison.Ordinal)) return t;
            }
        }
        return null;
    }

    private static bool ShouldSkipAssemblyForScan(string asmName)
    {
        if (string.IsNullOrEmpty(asmName)) return false;
        if (asmName.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("System", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("Microsoft", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("BepInEx", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("MonoMod", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("HarmonyX", StringComparison.Ordinal) || asmName == "0Harmony") return true;
        if (asmName.StartsWith("mscorlib", StringComparison.Ordinal) || asmName.StartsWith("netstandard", StringComparison.Ordinal)) return true;
        return false;
    }
}
