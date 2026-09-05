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
/// <para>The previewed WEAPON SKIN is not a wear piece: it is injected into the social data itself
/// (<c>socialData.professionData.weaponSkin</c>) BEFORE the model is generated — see
/// <c>WeaponSkinSourceLua</c> for the derivation and why the display-override attr alone did not work —
/// and the weapon the social data brought with it is then removed from the CMount attachment mount, the
/// de-dupe every game view that shows a weapon skin on a preview model performs (see
/// <c>WeaponMountClearLua</c>). Every preview writes a one-line outcome the host logs unconditionally.</para>
/// </summary>
internal sealed class PandaWardrobePreviewProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string ChunkName = "stellar.wardrobe.preview";
    private const string ModelGlobal = "__stellar_wardrobe_preview_model";
    private const string WeaponGlobal = "__stellar_wardrobe_preview_weapon";

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;

    private bool _resolved;
    private bool _failLogged;
    private bool _weaponLogged;
    private MethodInfo? _mainStateGetter;
    private MethodInfo? _doString;
    private MethodInfo? _luaGetGlobal;
    private MethodInfo? _toVariant;
    private MethodInfo? _luaToString;
    private MethodInfo? _luaPop;

    public PandaWardrobePreviewProbe(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Create the preview model for <paramref name="charId"/> dressed with <paramref name="outfit"/>
    /// (async — lands in the Lua global a few frames later). No-op if the Lua bridge is unresolved.</summary>
    public void BuildModel(int charId, IReadOnlyDictionary<int, int> outfit, IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>>? dyes)
    {
        _weaponLogged = false;   // one weapon-outcome line per preview, emitted when the model lands
        Run(BuildModelChunk(charId, outfit, dyes), $"[WardrobePreview] BuildModel({charId})");
    }

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
            if (model is not null) LogWeaponOutcome(state);
            return model;
        }
        catch (Exception ex)
        {
            WarnOnce($"TryTakeModel threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // The Lua chunk records what it did to the weapon in a string global; surface it ONCE per preview,
    // UNCONDITIONALLY (a hover is a user action — one line is affordable, and diagnostics are off on the
    // owner's client, which is exactly when a silent no-op is unfalsifiable). Shapes:
    //   weapon none                                       — no 731 key reached the framework
    //   weapon skin=7320012 social=set(prof=2,was=0) mount=cleared(L:…,R:…,skinModels:…) disp=ok
    //                                                     — skin injected, the social CMount weapon removed
    //   weapon skin=… social=no-professionData …          — social data carried no profession block
    //   weapon skin=… mount=skip(skin0) …                 — resolved default look: nothing to de-dupe
    //   weapon skin=… social=err:… | mount=err:… | disp=err:…  — that Lua call threw (message included)
    private void LogWeaponOutcome(object state)
    {
        if (_weaponLogged || _luaToString is null) return;
        _weaponLogged = true;
        try
        {
            _luaGetGlobal!.Invoke(state, new object[] { WeaponGlobal });
            var text = _luaToString.Invoke(state, new object[] { -1 }) as string;
            _luaPop!.Invoke(state, new object[] { 1 });
            _log.Info($"[WardrobePreview] weapon {(string.IsNullOrEmpty(text) ? "unreported" : text)}");
        }
        catch (Exception ex)
        {
            WarnOnce($"weapon outcome read threw: {ex.GetType().Name}: {ex.Message}");
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

    // Recipe = fetch the self social data, inject the previewed weapon skin INTO it, create the model from
    // it, dress it with the outfit + each piece's LIVE dye, then (weapon-skin previews only) clear the
    // CMount weapon the social data brought with it so the model carries ONE weapon. Split into head /
    // weapon / gen / dress / mount / tail to stay under the method-size gate.
    internal static string BuildModelChunk(int charId, IReadOnlyDictionary<int, int> outfit, IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>>? dyes)
    {
        var hasSkin = outfit.TryGetValue(WardrobeRegions.WeaponSkinPreview, out var skinId);
        return CoroHead(charId) + (hasSkin ? WeaponSkinSourceLua(skinId) : string.Empty) + GenLua() +
               DressLua(outfit, dyes) +
               (hasSkin ? WeaponMountClearLua + WeaponSkinOverrideLua : string.Empty) +
               "    rawset(_G, '" + ModelGlobal + "', m)\n" +
               "  end)\n" +
               "  coroFn()\n" +
               "end)\n" +
               "if not ok and logError then logError('[WardrobePreview.lua] err: ' .. tostring(err)) end";
    }

    // pcall + coroutine + social data fetch. The weapon-outcome global is armed to 'none' here so the
    // BepInEx log always answers "did a 731 key reach the framework for this hover?" — the previous build
    // could not be judged from the owner's log at all.
    private static string CoroHead(int charId) =>
        "local ok, err = pcall(function()\n" +
        "  local coroFn = ((Z.CoroUtil).create_coro_xpcall)(function()\n" +
        "    rawset(_G, '" + WeaponGlobal + "', 'none')\n" +
        "    local socialVM = ((Z.VMMgr).GetVM)('social')\n" +
        "    if not socialVM then logError('[WardrobePreview.lua] no socialVM') return end\n" +
        "    local socialData = (socialVM.AsyncGetSocialData)(0, " + charId.ToString(CultureInfo.InvariantCulture) + ", (ZUtil.ZCancelSource).NeverCancelToken)\n" +
        "    if not socialData then logError('[WardrobePreview.lua] socialData nil') return end\n";

    // Model creation from the (possibly re-skinned) social data + the gender-named idle clip.
    private static string GenLua() =>
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
            // 731 is a preview-only key and is NOT a FashionWear piece — it must never reach the zList.
            if (kv.Value == 0 || kv.Key == WardrobeRegions.WeaponSkinPreview) continue;
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

    // PRIMARY mechanism — re-skin the SOURCE, before the model exists.
    // The weapon skin rides a SEPARATE system: the game's own GetFashionWearList skips region 731
    // (fashion_vm.lua:454), so EWearFashion can never carry it. The 2.6.0 build instead used the display
    // OVERRIDE attr, copying the game's weapon-skin tab — and the owner's in-game test (2026-09-05) showed
    // the preview keeping the live weapon for EVERY outfit. That is consistent with the RE: all three
    // display-override call sites dress the CACHED PLAYER model (fashion_system_view.initPlayerModel:831,
    // shop_fashion_sub_view.initPlayerModel:300, competency_rating_main_view.createPlayerModel:253 all call
    // Z.UnrealSceneMgr:GetCachePlayerModel), never a GenModelByLuaSocialData model.
    // A social-data model's appearance comes from the social data itself: Panda.Script exposes
    // SetAttrByLuaSocialData with setFace/setFashion/setEquip/setSetting sub-setters — and the weapon SKIN
    // is not in any of those. It is in SocialData.professionData, a two-field message
    // {profession_id, weapon_skin} (stru_profession_data.proto), which the server fills for
    // SocialDataType.SocialDataTypeWeapon (const_value.lua:117) — i.e. "profession data" IS the weapon data.
    // socialData is a plain Lua table (world_proxy.GetSocialData:21831 returns pb.decode output, camelCase
    // keys — the encode side writes charId for char_id, the decode side reads errCode for err_code), so the
    // field is directly writable before the model is generated. Setting the source also dodges the race the
    // override had: the social weapon streams in asynchronously AFTER the attr was written.
    // Skin 0 means "the class's default look": the apply path resolves it through GetWeaponOriginSkinId
    // (weapon_skill_skin_vm.AsyncUseProfessionSkin:199), so the preview mirrors that step for step rather
    // than leaving the currently-worn skin on the model and lying about what applying would do.
    private static string WeaponSkinSourceLua(int skinId) =>
        "    local skin=" + skinId.ToString(CultureInfo.InvariantCulture) + "\n" +
        "    local wok, werr = pcall(function()\n" +
        "      if skin == 0 then\n" +
        "        local wvm=((Z.VMMgr).GetVM)('weapon')\n" +
        "        local svm=((Z.VMMgr).GetVM)('weapon_skill_skin')\n" +
        "        if wvm and svm then skin = svm:GetWeaponOriginSkinId((wvm.GetCurWeapon)()) end\n" +
        "      end\n" +
        "      local pd = socialData.professionData\n" +
        "      if pd == nil then rawset(_G, '" + WeaponGlobal + "', 'skin=' .. tostring(skin) .. ' social=no-professionData') return end\n" +
        "      rawset(_G, '" + WeaponGlobal + "', 'skin=' .. tostring(skin) .. ' social=set(prof=' .. tostring(pd.professionId) .. ',was=' .. tostring(pd.weaponSkin) .. ')')\n" +
        "      pd.weaponSkin = skin\n" +
        "    end)\n" +
        "    if not wok then rawset(_G, '" + WeaponGlobal + "', 'skin=' .. tostring(skin) .. ' social=err:' .. tostring(werr))" +
        " logError('[WardrobePreview.lua] weapon skin source err: ' .. tostring(werr)) end\n";

    // The DE-DUPE, and the fix for the owner's 2026-09-05 double-weapon report ("it show with currently
    // weapon using (which should be hidden), it cause previewer show player have 2 weapons skin rendered").
    // A social-data model carries TWO independent weapon renderers:
    //   * the CMount attachment — EModelCMountWeaponL/R, string model paths that arrive WITH the social
    //     data (Panda.ZGame.WeaponOriginData bundles ModelCMountWeaponL/R beside WeaponSkinId/MainModelId);
    //   * Panda.ZGame.WeaponModelComp, which owns its OWN weapon GameObjects (fields mainWeaponModel_ /
    //     subWeaponModel_, getWeaponMount/getMountName/clearModel) and is driven by the profession+skin
    //     data — i.e. by the socialData.professionData.weaponSkin we inject above.
    // That is why EVERY game view showing a weapon skin on a preview model clears the CMount first and only
    // then asks for the skin: fashion_system_view.initPlayerModel:844-845 (the wardrobe screen that hosts
    // the weapon-skin tab, whose SelectStyle:26 sets the display override) and shop_fashion_sub_view
    // .initPlayerModel:319-320 (ShowPlayerWeaponModel:275 sets it) — plus every weaponless social view
    // (investigation_clue_window_view:942/944, rank_main_view:503/637, face_edit_view:197,
    // talk_model_window_view:307). Without the clear both renderers draw and the model wears two weapons.
    // Guards, both load-bearing:
    //   * emitted ONLY when the outfit carries a weapon skin — a legacy outfit (no 731 key) must keep
    //     showing the worn weapon exactly as before;
    //   * skipped when the resolved skin is 0 (GetWeaponOriginSkinId's end-of-chain fallback), because 0
    //     asks for no display weapon and clearing the mount as well would leave the model unarmed.
    // The pre-clear values are REPORTED (with the skin's own WeaponSkinTable.WeaponModelId list beside
    // them, the ids equip_vm.CreateEquipModel:139 builds weapon models from) so one owner log settles which
    // renderer holds which weapon; the read is in its own pcall so a read failure can never block the clear.
    private const string WeaponMountClearLua =
        "    local mok, merr = pcall(function()\n" +
        "      local function say(s) rawset(_G, '" + WeaponGlobal + "', tostring(rawget(_G, '" + WeaponGlobal + "')) .. s) end\n" +
        "      if skin == 0 then say(' mount=skip(skin0)') return end\n" +
        "      local l, r, wm = 'na', 'na', 'na'\n" +
        "      pcall(function()\n" +
        "        local function mv(a)\n" +
        "          local v = m:GetLuaAttr(a)\n" +
        "          if v == nil then return 'nil' end\n" +
        "          local okv, vv = pcall(function() return v.Value end)\n" +
        "          local s = tostring((okv and vv ~= nil) and vv or v)\n" +
        "          if #s > 40 then s = s:sub(#s - 39) end\n" +
        "          return s\n" +
        "        end\n" +
        "        l = mv((Z.ModelAttr).EModelCMountWeaponL) r = mv((Z.ModelAttr).EModelCMountWeaponR)\n" +
        "      end)\n" +
        "      pcall(function()\n" +
        "        local row = ((Z.TableMgr).GetRow)('WeaponSkinTableMgr', skin)\n" +
        "        if row ~= nil and row.WeaponModelId ~= nil then\n" +
        "          local t = {} for _, id in ipairs(row.WeaponModelId) do t[#t + 1] = tostring(id) end\n" +
        "          wm = table.concat(t, '/')\n" +
        "        end\n" +
        "      end)\n" +
        "      m:SetLuaAttr((Z.ModelAttr).EModelCMountWeaponL, '')\n" +
        "      m:SetLuaAttr((Z.ModelAttr).EModelCMountWeaponR, '')\n" +
        "      say(' mount=cleared(L:' .. l .. ',R:' .. r .. ',skinModels:' .. wm .. ')')\n" +
        "    end)\n" +
        "    if not mok then rawset(_G, '" + WeaponGlobal + "', tostring(rawget(_G, '" + WeaponGlobal + "')) .. ' mount=err:' .. tostring(merr))" +
        " logError('[WardrobePreview.lua] weapon mount clear err: ' .. tostring(merr)) end\n";

    // SECONDARY mechanism, kept as belt-and-braces — the display override the 2.6.0 build shipped alone.
    // It is a no-op-or-win and CANNOT double-draw: WeaponModelComp holds a single mainWeaponModel_ /
    // subWeaponModel_ pair and clearModel()s before loading, and the override asks for the SAME skin the
    // source injection already baked in. It is kept because it is the ONE weapon mechanism the game
    // exercises by hand, so if the source injection ever stops reaching WeaponModelComp this still answers;
    // it is emitted AFTER the mount clear so the last word on this model is "one weapon, the saved skin".
    // 2.6.0 shipped it alone and the owner saw the live weapon on every outfit, so it is not the mechanism.
    // Unlike 2.6.0 the failure is REPORTED (a bare pcall hid a nil attr / missing method / thrown exception),
    // so the owner's next log names which of the mechanisms answered.
    private const string WeaponSkinOverrideLua =
        "    local aok, aerr = pcall(function() m:SetLuaIntAttr((Z.ModelAttr).EModelDisplayWeaponSkinId, skin) end)\n" +
        "    rawset(_G, '" + WeaponGlobal + "', tostring(rawget(_G, '" + WeaponGlobal + "')) .. (aok and ' disp=ok' or (' disp=err:' .. tostring(aerr))))\n" +
        "    if not aok then logError('[WardrobePreview.lua] weapon skin disp err: ' .. tostring(aerr)) end\n";

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
        "  rawset(_G, '" + WeaponGlobal + "', nil)\n" +
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
        _luaToString = FindMethod(luaStateType, "LuaToString", typeof(int));   // LuaInterface.LuaStatePtr
        _luaPop = FindMethod(luaStateType, "LuaPop", typeof(int));
        if (_mainStateGetter is null || _doString is null)
        {
            WarnOnce("LuaState.mainState / DoString(string,string) not found");
            return false;
        }
        if (_luaGetGlobal is null || _toVariant is null || _luaPop is null)
            WarnOnce("LuaGetGlobal/ToVariant/LuaPop not found — model handoff to C# disabled");

        if (_luaToString is null) WarnOnce("LuaToString(int) not found — weapon-skin outcome reporting disabled");

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
