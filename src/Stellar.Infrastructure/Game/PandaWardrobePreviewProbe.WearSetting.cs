using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

// The outfit's OWN show/hide state on the preview model (framework 2.31.0, IWardrobePreview.Show(..., hidden)).
// A GenModelByLuaSocialData model applies the player's CURRENT global setting from the social data
// (setSettingAttrByLuaSocialData), so a piece hidden globally right now could not be previewed even when the outfit
// shows it. A model's own visibility is the model attr Z.LocalAttr.EWearSetting — the same "region=state|…" string the
// global setting (settingMap[201]) uses, state 2 = hidden, 1 = shown, "" = show all. The game writes it on its own
// preview models AFTER dressing: face_change_body_view.lua:211 (a settingStr) and cont_bpcard_pass_award_view.lua:579
// (""), so this block runs right after the EWearFashion dress.
// ⚠️ The live client's Lua is known to differ from the release_3.7 extract (KB Fashion-Show-Hide-State.md "LIVE client
// differs"), so the write is pcall-guarded and its outcome — plus a read-back of the attr — is appended to the
// probe's one-line weapon-outcome log, so one in-game hover proves whether the model took it.
internal sealed partial class PandaWardrobePreviewProbe
{
    /// <summary>The EWearSetting string for a per-region hidden map: ALL 14 <see cref="WardrobeRegions.All"/> regions,
    /// each explicit (<c>2</c> hidden, <c>1</c> shown — a region missing from the map is shown), in the stable region
    /// order, each terminated by <c>|</c> exactly like the game's <c>RegionDictToSettingStr</c>. Explicit for every
    /// region on purpose: the live client was seen to treat a partial string as the WHOLE setting (KB, the "713=2|"
    /// incident), and the model must not keep any of the player's global hides. Keys outside the 14 are ignored.</summary>
    internal static string BuildWearSetting(IReadOnlyDictionary<int, bool> hidden)
    {
        var sb = new StringBuilder(WardrobeRegions.All.Count * 6);
        foreach (var region in WardrobeRegions.All)
        {
            var isHidden = hidden.TryGetValue(region, out var h) && h;
            sb.Append(region.ToString(CultureInfo.InvariantCulture)).Append('=').Append(isHidden ? '2' : '1').Append('|');
        }
        return sb.ToString();
    }

    // `setting` is built by BuildWearSetting from ints we control (no injection). Every statement starts with a keyword
    // or an identifier — never with `(` — so nothing can be parsed as a continuation of the previous call.
    // The read-back is its own pcall (GetLuaAttr on a LocalAttr is unproven on the live client) and can never undo
    // the write; 'na' = the read itself failed, 'same' = the model holds exactly what we wrote.
    private static string WearSettingLua(string setting) =>
        "    local hs = '" + setting + "'\n" +
        "    local hok, herr = pcall(function() m:SetLuaAttr((Z.LocalAttr).EWearSetting, hs) end)\n" +
        "    local hrb = 'na'\n" +
        "    if hok then pcall(function()\n" +
        "      local v = m:GetLuaAttr((Z.LocalAttr).EWearSetting)\n" +
        "      if v == nil then hrb = 'nil' return end\n" +
        "      local okv, vv = pcall(function() return v.Value end)\n" +
        "      local s = tostring((okv and vv ~= nil) and vv or v)\n" +
        "      if s == hs then hrb = 'same' else hrb = s:sub(1, 80) end\n" +
        "    end) end\n" +
        "    rawset(_G, '" + WeaponGlobal + "', tostring(rawget(_G, '" + WeaponGlobal + "')) .. (hok and (' hide=ok(rb=' .. hrb .. ')') or (' hide=err:' .. tostring(herr))))\n" +
        "    if not hok then logError('[WardrobePreview.lua] wear setting err: ' .. tostring(herr)) end\n";
}
