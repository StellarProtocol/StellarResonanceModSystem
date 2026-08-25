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
    public void BuildModel(int charId, IReadOnlyDictionary<int, int> outfit)
        => Run(BuildModelChunk(charId, outfit), $"[WardrobePreview] BuildModel({charId})");

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
    internal static string BuildModelChunk(int charId, IReadOnlyDictionary<int, int> outfit)
        => Preamble(charId) + DressLua(outfit) +
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

    // The dress block: build a ZList of SingleWearData from the outfit (skip 0) WITH each piece's live dye
    // (BaseColor over Base1..UnderWear4, AttachmentColor over Socks1..Socks4) from fashion_data:GetColor —
    // a faithful replica of fashion_vm's getFashionColorZList. Dye is per-piece (one per id), so reading it
    // live shows the player's real colours without any per-outfit storage. Fashion ids are ints (no injection).
    private static string DressLua(IReadOnlyDictionary<int, int> outfit)
    {
        var wear = new StringBuilder();
        foreach (var kv in outfit)
        {
            if (kv.Value == 0) continue;
            var id = kv.Value.ToString(CultureInfo.InvariantCulture);
            wear.Append("      do local wd=(((Panda.ZGame).SingleWearData).Rent)() wd.FashionID=").Append(id).Append(" wd.SlotID=0\n")
                .Append("        pcall(function() wd.BaseColor=colz(").Append(id).Append(",(E.EFashionColorAreaType).Base1,(E.EFashionColorAreaType).UnderWear4) end)\n")
                .Append("        pcall(function() wd.AttachmentColor=colz(").Append(id).Append(",(E.EFashionColorAreaType).Socks1,(E.EFashionColorAreaType).Socks4) end)\n")
                .Append("        zList:Add(wd) end\n");
        }
        return "    pcall(function()\n" + DyeHelperLua + wear +
               "      m:SetLuaAttr((Z.LocalAttr).EWearFashion, zList)\n" +
               "    end)\n";
    }

    // Opens the dress pcall's zList + the colz(fid,a0,a1) helper (per-piece RGB dye ZList).
    private const string DyeHelperLua =
        "      local zList=((((ZUtil.Pool).Collections).ZList_Panda_ZGame_SingleWearData).Rent)()\n" +
        "      local fd=(Z.DataMgr.Get)('fashion_data')\n" +
        "      local function colz(fid,a0,a1)\n" +
        "        local zl=((((ZUtil.Pool).Collections).ZList_UnityEngine_Vector3).Rent)()\n" +
        "        local cd=fd and fd:GetColor(fid)\n" +
        "        for area=a0,a1 do\n" +
        "          local hsv\n" +
        "          if cd and cd[area] then hsv=cd[area] else\n" +
        "            hsv=(Z.ColorHelper.GetDefaultHSV)()\n" +
        "            pcall(function() local dl=(Z.LuaBridge.GetFashionDefaultHSVListByFashionId)(fid)\n" +
        "              if dl then if area<dl.count then local v=dl[area] hsv={h=(math.floor)(v.x*360+0.5),s=(math.floor)(v.y*100+0.5),v=(math.floor)(v.z*100+0.5)} end dl:Recycle() end end)\n" +
        "          end\n" +
        "          local rgb=(Color.HSVToRGB)(hsv.h/360, hsv.s/100, hsv.v/100, true)\n" +
        "          zl:Add((Vector3.New)(rgb.r, rgb.g, rgb.b))\n" +
        "        end\n" +
        "        zl:Insert(0, Vector3.zero)\n" +
        "        return zl\n" +
        "      end\n";

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
