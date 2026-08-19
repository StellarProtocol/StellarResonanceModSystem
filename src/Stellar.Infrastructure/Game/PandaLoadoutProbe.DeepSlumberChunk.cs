namespace Stellar.Infrastructure.Game;

/// <summary>
/// The Deep-Slumber (season cultivate) Lua-chunk fragment <c>PandaLoadoutProbe.Resolution.cs</c>'s
/// <c>RefreshChunk</c> appends to its dump. Split into its own partial purely for file-size (the
/// STELLAR size guardrail — <c>Resolution.cs</c> was pushing past 500 LoC); this is chunk-building
/// logic, not diagnostic logging, so it does NOT belong in a <c>.Diagnostics.cs</c> partial.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    // Deep-Slumber Psychoscope (season cultivate) — owner-verified gap (2026-08-19), FIXED
    // 2026-08-20 for a SECOND time with actual evidence (owner run sea/O1jJepsgKC, fresh session
    // on c61e29f: no deepSlumber block uploaded at all — no DSLV row, so ParseDeepSlumber returned
    // null). This reads the LUA mirror — populated at login, the same source the game's own season
    // views read — NOT the C# reflection mirror (PandaInventoryPullReader.ReadDeepSlumber), which
    // populates the same containers LAZILY (empty until the player opens the Psychoscope UI at
    // least once this session).
    //
    // ROOT CAUSE (confirmed against the decompiled containers,
    // data/StarResonanceData/lua/zcontainer/{season_cultivate_line_data,cultivate_line_data,
    // cultivate_line_sub_type_data,cultivate_area_data,cultivate_big_node_data,
    // cultivate_middle_node_data,cultivate_normal_node_data,season_role_level_data,
    // season_role_level}.lua): every one of these zcontainer maps is wrapped by the game's own
    // setForbidenMt, whose __pairs iterator (stateless_iter) sets "local v = nil" and NEVER fetches
    // a real value — it only ever yields (key, nil). The PREVIOUS chunk here did
    // "for k,v in pairs(m) do ... v ... end", so v was nil at every map level and the whole DS walk
    // silently produced nothing (pcall never even had anything to fail on — there was no error,
    // just an empty result). The game's own view-model code never trusts that loop value: it
    // iterates KEYS ONLY via pairs, then fetches each value by INDEXING (map[k], which resolves
    // through __index = t.__data__ — an ordinary table read, unaffected by the __pairs bug). This
    // chunk now does the same at EVERY map level (season levels, lines, subtypes, areas, and the
    // three node maps): "for k in pairs(m) do local v = m[k] if v ~= nil then ... end end". Field
    // names verified against the decompiled containers: season_role_level.level,
    // cultivate_big_node_data.fantasyId, cultivate_middle_node_data.itemId,
    // cultivate_normal_node_data.activeLevel, cultivate_area_data.{isActive,activateEffectScore}
    // (plain fields — a single container field access always worked; only MAP iteration was bitten
    // by __pairs).
    //
    // Two INDEPENDENT pcalls (season levels / cultivate lines) so a missing container on one side
    // still yields the other's rows — never breaks the rest of this dump. No more silent failures:
    // each pcall's ok/err is captured explicitly — a failure appends a "DSERR\t<section>\t<msg>"
    // row — and the cultivate-line walk always appends "DSN\t<lineCount>", the number of top-level
    // seasonCultivateLineMap entries the outer walk actually iterated, so the NEXT dump tells us
    // exactly which level of the walk produced nothing instead of just an absent block. DSERR/DSN
    // rows are ignored by ParseDeepSlumber for state-building (unknown row prefix); they exist for
    // the diagnostics partial (PandaLoadoutProbe.DeepSlumber.Diagnostics.cs).
    private const string DeepSlumberChunkFragment =
        // "DSLV\t<seasonId>:<level>,..." — cs.seasonRoleLevelData.seasonRoleLevelMap. Always appended
        // once this walk runs, even when the payload ends up empty.
        " local dslv=\"\"" +
        " local dslvOk,dslvErr=pcall(function()" +
        "  local srl=(cs.seasonRoleLevelData) and (cs.seasonRoleLevelData).seasonRoleLevelMap" +
        "  if srl~=nil then" +
        "   for sid in pairs(srl) do" +
        "    local sl=srl[sid]" +
        "    if sl~=nil then dslv=(dslv==\"\" and \"\" or dslv..\",\")..tostring(sid)..\":\"..tostring(sl.level or 0) end" +
        "   end" +
        "  end" +
        " end)" +
        " out=out..\"\\nDSLV\\t\"..dslv" +
        " if not dslvOk then out=out..\"\\nDSERR\\tseasonLevel\\t\"..tostring(dslvErr) end" +
        // One "DSA\t<lineId>\t<subType>\t<areaId>\t<0|1 active>\t<score>\t<big>\t<middle>\t<normal>" row per
        // (lineId, subType, areaId) variant — cs.seasonCultivateLineData.seasonCultivateLineMap ->
        // cultivateLineMap (by subType) -> cultivateLineDataMap (by areaId); each node map serialized as
        // "nodeId:value,..." (fantasyId / itemId / activeLevel for big / middle / normal respectively).
        // Every map level below is walked keys-first (pairs) then value-indexed (map[k]) per the
        // root-cause fix above — a bare loop-value is never trusted again anywhere in this chunk.
        " local dsn=0" +
        " local dsaOk,dsaErr=pcall(function()" +
        "  local scl=(cs.seasonCultivateLineData) and (cs.seasonCultivateLineData).seasonCultivateLineMap" +
        "  if scl~=nil then" +
        "   for lid in pairs(scl) do" +
        "    dsn=dsn+1" +
        "    local ld=scl[lid]" +
        "    if ld~=nil then" +
        "     local clm=ld.cultivateLineMap" +
        "     if clm~=nil then" +
        "      for st in pairs(clm) do" +
        "       local subd=clm[st]" +
        "       if subd~=nil then" +
        "        local cldm=subd.cultivateLineDataMap" +
        "        if cldm~=nil then" +
        "         for aid in pairs(cldm) do" +
        "          local ar=cldm[aid]" +
        "          if ar~=nil then" +
        "           local active=(ar.isActive) and 1 or 0" +
        "           local score=ar.activateEffectScore or 0" +
        "           local big=\"\" local bm=ar.cultivateBigNodeMap" +
        "           if bm~=nil then for nid in pairs(bm) do local nv=bm[nid] if nv~=nil then big=(big==\"\" and \"\" or big..\",\")..tostring(nid)..\":\"..tostring(nv.fantasyId or 0) end end end" +
        "           local mid=\"\" local mm=ar.cultivateMiddleNodeMap" +
        "           if mm~=nil then for nid in pairs(mm) do local nv=mm[nid] if nv~=nil then mid=(mid==\"\" and \"\" or mid..\",\")..tostring(nid)..\":\"..tostring(nv.itemId or 0) end end end" +
        "           local nor=\"\" local nm=ar.cultivateNormalNodeMap" +
        "           if nm~=nil then for nid in pairs(nm) do local nv=nm[nid] if nv~=nil then nor=(nor==\"\" and \"\" or nor..\",\")..tostring(nid)..\":\"..tostring(nv.activeLevel or 0) end end end" +
        "           out=out..\"\\nDSA\\t\"..tostring(lid)..\"\\t\"..tostring(st)..\"\\t\"..tostring(aid)..\"\\t\"..tostring(active)..\"\\t\"..tostring(score)..\"\\t\"..big..\"\\t\"..mid..\"\\t\"..nor" +
        "          end" +
        "         end" +
        "        end" +
        "       end" +
        "      end" +
        "     end" +
        "    end" +
        "   end" +
        "  end" +
        " end)" +
        " out=out..\"\\nDSN\\t\"..tostring(dsn)" +
        " if not dsaOk then out=out..\"\\nDSERR\\tcultivateLines\\t\"..tostring(dsaErr) end";
}
