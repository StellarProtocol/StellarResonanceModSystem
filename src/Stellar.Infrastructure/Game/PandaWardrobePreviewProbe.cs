using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Creates a self character model dressed with an ARBITRARY saved outfit for the wardrobe 3D preview —
/// the same pipeline as <see cref="PandaPortraitModelProbe"/> (<c>social.AsyncGetSocialData</c> →
/// <c>Z.ModelManager:GenModelByLuaSocialData</c>) PLUS one re-dress call that puts the passed outfit on
/// the model: <c>m:SetLuaAttr((Z.LocalAttr).EWearFashion, &lt;ZList of Panda.ZGame.SingleWearData&gt;)</c>
/// — the exact mechanism the game's own wardrobe screen uses (<c>fashion_vm.RefreshWearAttr</c>). Verified
/// in-game (2026-08-25 spike). A SEPARATE class + Lua global from the portrait probe so the two never
/// collide. The model is stashed in a Lua global; <see cref="TryTakeModel"/> pulls it back for
/// <see cref="PortraitModelHost"/> to render.
/// </summary>
internal sealed class PandaWardrobePreviewProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string ChunkName = "stellar.wardrobe.preview";
    private const string ModelGlobal = "__stellar_wardrobe_preview_model";

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;

    private bool _resolved;
    private bool _failLogged;
    private MethodInfo? _mainStateGetter;
    private MethodInfo? _doString;
    private MethodInfo? _luaGetGlobal;
    private MethodInfo? _toVariant;
    private MethodInfo? _luaPop;

    public PandaWardrobePreviewProbe(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Create the preview model for <paramref name="charId"/> dressed with <paramref name="outfit"/>
    /// (async — lands in the Lua global a few frames later). No-op if the Lua bridge is unresolved.</summary>
    public void BuildModel(int charId, IReadOnlyDictionary<int, int> outfit, IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>>? dyes)
        => Run(BuildModelChunk(charId, outfit, dyes), $"[WardrobePreview] BuildModel({charId})");

    /// <summary>Recycle the preview model through the game's pool and clear the global.</summary>
    public void ClearModel() => Run(BuildClearChunk(), null);

    /// <summary>Fetch the created <c>ZModel</c> from the Lua global, or null while creation is in flight.</summary>
    public object? TryTakeModel()
    {
        if (!_resolved || _luaGetGlobal is null || _toVariant is null || _luaPop is null) return null;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) return null;
        try
        {
            _luaGetGlobal.Invoke(state, new object[] { ModelGlobal });
            var model = _toVariant.Invoke(state, new object[] { -1 });
            _luaPop.Invoke(state, new object[] { 1 });
            return model;
        }
        catch (Exception ex)
        {
            WarnOnce($"TryTakeModel threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void Run(string chunk, string? what)
    {
        if (!TryResolve()) return;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnOnce("LuaState.mainState was null"); return; }
        try
        {
            _doString!.Invoke(state, new object[] { chunk, ChunkName });
            if (what != null) _log.Info(what);
        }
        catch (Exception ex)
        {
            WarnOnce($"DoString threw ({what ?? "clear"}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Recipe = create the self model (social data), then dress it with the outfit + each piece's LIVE dye.
    // Split into preamble / dress / tail to stay under the method-size gate.
    internal static string BuildModelChunk(int charId, IReadOnlyDictionary<int, int> outfit, IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>>? dyes)
        => Preamble(charId) + DressLua(outfit, dyes) +
           "    rawset(_G, '" + ModelGlobal + "', m)\n" +
           "  end)\n" +
           "  coroFn()\n" +
           "end)\n" +
           "if not ok and logError then logError('[WardrobePreview.lua] err: ' .. tostring(err)) end";

    // Model creation up to (but not including) the dress: social data → GenModelByLuaSocialData → idle clip.
    private static string Preamble(int charId) =>
        "local ok, err = pcall(function()\n" +
        "  local coroFn = ((Z.CoroUtil).create_coro_xpcall)(function()\n" +
        "    local socialVM = ((Z.VMMgr).GetVM)('social')\n" +
        "    if not socialVM then logError('[WardrobePreview.lua] no socialVM') return end\n" +
        "    local socialData = (socialVM.AsyncGetSocialData)(0, " + charId.ToString(CultureInfo.InvariantCulture) + ", (ZUtil.ZCancelSource).NeverCancelToken)\n" +
        "    if not socialData then logError('[WardrobePreview.lua] socialData nil') return end\n" +
        "    local m = (Z.ModelManager):GenModelByLuaSocialData(socialData)\n" +
        "    if not m then logError('[WardrobePreview.lua] gen nil') return end\n" +
        "    local clip = 'as_f_base_idle'\n" +
        "    pcall(function() if ((socialData.basicData).gender) ~= (Z.PbEnum)('EGender', 'GenderMale') then clip = 'as_m_base_idle' end end)\n" +
        "    pcall(function() m:SetLuaAttr((Z.ModelAttr).EModelAnimOverrideByName, ((Z.AnimBaseData).Rent)(clip, ((Panda.ZAnim).EAnimBase).EIdle)) end)\n";

    // The dress block: a ZList of SingleWearData from the outfit (skip 0). When per-area dyes for a region
    // were captured (region → area → RGB, each channel 0..1, from IEntityDetail.GetFashion at save time —
    // the same source the Entity Inspector reads), the piece is tinted PER AREA (see AppendPiece); absent
    // regions render in the fashion's default colour. Fashion ids + colour floats are values we control
    // (InvariantCulture) — no injection. Colour access pcall-guarded.
    private static string DressLua(IReadOnlyDictionary<int, int> outfit, IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>>? dyes)
    {
        var wear = new StringBuilder();
        foreach (var kv in outfit)
        {
            if (kv.Value == 0) continue;
            IReadOnlyDictionary<int, float[]>? areaMap = null;
            dyes?.TryGetValue(kv.Key, out areaMap);
            AppendPiece(wear, kv.Key, kv.Value, areaMap);
        }
        return "    pcall(function()\n" +
               "      local zList=((((ZUtil.Pool).Collections).ZList_Panda_ZGame_SingleWearData).Rent)()\n" +
               wear +
               "      m:SetLuaAttr((Z.LocalAttr).EWearFashion, zList)\n" +
               "    end)\n";
    }

    // One SingleWearData for a piece. SlotID MUST be the piece's FashionRegion — exactly what the game's own
    // fashion_vm.GetFashionWearList does (`data.SlotId = region`): the model routes a head piece to its mount
    // by SlotID (713 Headwear → EModelCMountHeadWearWearData, 718 HeadWearSecond → …HeadWear2WearData), so
    // with SlotID=0 both head accessories collapsed onto ONE mount and the second overwrote the first
    // (Discord report 2026-09-03: "briefly see head slot 1, then overwritten by head slot 2"). When per-area
    // dyes were captured, place each colour on its real EFashionColorAreaType area exactly as fashion_vm does:
    // BaseColor is a 17-slot ZList indexed by area (index 0 a zero placeholder, 1..16 the areas),
    // AttachmentColor a 5-slot socks list ([zero, Socks1..4] = areas 5..8). Undyed areas stay zero → default.
    private static void AppendPiece(StringBuilder wear, int region, int fashionId, IReadOnlyDictionary<int, float[]>? areaMap)
    {
        wear.Append("      do local wd=(((Panda.ZGame).SingleWearData).Rent)() wd.FashionID=")
            .Append(fashionId.ToString(CultureInfo.InvariantCulture)).Append(" wd.SlotID=")
            .Append(region.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (areaMap is { Count: > 0 })
        {
            var tbl = new StringBuilder();
            foreach (var ac in areaMap)
                if (ac.Value.Length >= 3)
                    tbl.Append('[').Append(ac.Key.ToString(CultureInfo.InvariantCulture)).Append("]=(Vector3.New)(")
                       .Append(F(ac.Value[0])).Append(',').Append(F(ac.Value[1])).Append(',').Append(F(ac.Value[2])).Append("),");
            wear.Append("        pcall(function() local dye={").Append(tbl).Append("}\n")
                .Append("          local function rent() return ((((ZUtil.Pool).Collections).ZList_UnityEngine_Vector3).Rent)() end\n")
                .Append("          local bc=rent() for a=0,16 do bc:Add(dye[a] or Vector3.zero) end\n")
                .Append("          local at=rent() at:Add(Vector3.zero) for a=5,8 do at:Add(dye[a] or Vector3.zero) end\n")
                .Append("          wd.BaseColor=bc wd.AttachmentColor=at end)\n");
        }
        wear.Append("        zList:Add(wd) end\n");
    }

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    internal static string BuildClearChunk() =>
        "pcall(function()\n" +
        "  local m = rawget(_G, '" + ModelGlobal + "')\n" +
        "  if m and Z.ModelManager then ((Z.ModelManager).RecycleModelByLua)(Z.ModelManager, m) end\n" +
        "  rawset(_G, '" + ModelGlobal + "', nil)\n" +
        "end)";

    private bool TryResolve()
    {
        if (_resolved) return true;
        var luaStateType = _types.FindType("ZLuaFramework.LuaState") ?? _types.FindType("LuaInterface.LuaState");
        if (luaStateType is null) return false;

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        _doString = FindMethod(luaStateType, "DoString", typeof(string), typeof(string));
        _luaGetGlobal = FindMethod(luaStateType, "LuaGetGlobal", typeof(string));
        _toVariant = FindMethod(luaStateType, "ToVariant", typeof(int));
        _luaPop = FindMethod(luaStateType, "LuaPop", typeof(int));
        if (_mainStateGetter is null || _doString is null)
        {
            WarnOnce("LuaState.mainState / DoString(string,string) not found");
            return false;
        }
        if (_luaGetGlobal is null || _toVariant is null || _luaPop is null)
            WarnOnce("LuaGetGlobal/ToVariant/LuaPop not found — model handoff to C# disabled");

        _resolved = true;
        _log.Info("[WardrobePreview] resolved: GenModelByLuaSocialData + EWearFashion dress via Lua bridge");
        return true;
    }

    private static MethodInfo? FindMethod(Type type, string name, params Type[] paramTypes)
    {
        foreach (var m in type.GetMethods(AnyInstance))
        {
            if (m.Name != name || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != paramTypes.Length) continue;
            var match = true;
            for (var i = 0; i < ps.Length; i++)
                if (ps[i].ParameterType != paramTypes[i]) { match = false; break; }
            if (match) return m;
        }
        return null;
    }

    private void WarnOnce(string msg)
    {
        if (_failLogged) return;
        _failLogged = true;
        _log.Warning($"[WardrobePreview] {msg}");
    }
}
