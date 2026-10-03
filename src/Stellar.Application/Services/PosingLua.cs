using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

/// <summary>The Lua the posing backend runs through <c>ILua</c> and the parsers for the answers. Calls are the game's own
/// (recon photo-posing-recon.md § 1 / run 5 (2); expression_vm.lua FacialIdConversion: <c>FaceDataId[1]</c> for
/// <c>EGender.GenderMale</c>, else <c>[2]</c>). Answers are <c>"ok &lt;value&gt;"</c> or <c>"err &lt;message&gt;"</c>.</summary>
internal static class PosingLua
{
    internal const string OutGlobal = "__stellar_pz_out";

    /// <summary>Runs <paramref name="body"/> under pcall and stores <c>"ok &lt;result&gt;"</c> / <c>"err &lt;message&gt;"</c>.</summary>
    internal static string Wrap(string body) =>
        "rawset(_G,'" + OutGlobal + "',nil); local _ok, _r = pcall(function() " + body + " end); " +
        "rawset(_G,'" + OutGlobal + "', _ok and ('ok ' .. tostring(_r)) or ('err ' .. tostring(_r)))";

    /// <summary>One line per unlocked expression (display type 4 = Emote, table Type 2): id TAB name TAB male face TAB
    /// female face — the same list call the action list uses (<c>EmoteLua.ListChunk</c>).</summary>
    internal const string ExpressionsChunk = @"
local vm = Z.VMMgr.GetVM('expression')
if not vm then return '' end
local out, seen = {}, {}
local list = vm.GetExpressionShowDataByType(4, false, nil, true) or {}
for _, d in ipairs(list) do
  local r = d.tableData
  if r and r.Type == 2 and d.activeType == 1 and r.FaceDataId and not seen[r.Id] then
    seen[r.Id] = true
    local name = (string.gsub(tostring(r.Name or ''), '[\t\n]', ' '))
    out[#out + 1] = tostring(r.Id) .. '\t' .. name .. '\t' .. tostring(r.FaceDataId[1] or 0) .. '\t' .. tostring(r.FaceDataId[2] or 0)
  end
end
return table.concat(out, '\n')";

    /// <summary>The game's photo-member limit as camera_member_vm AddMemberToList reads it: <c>PhotographTeamMemberLimit[1]</c>
    /// on the PC UI, else <c>[2]</c> (<c>{30, 10}</c> in release_3.7's table/gen/Global.lua); "0" when absent.</summary>
    internal const string MemberLimitChunk =
        "local l = Z.Global.PhotographTeamMemberLimit; if not l then return 0 end; " +
        "if Z.IsPCUI and l[1] then return l[1] end; return l[2] or 0";

    /// <summary>One line per uuid, in order: the entity's AttrName, or its NPC table name when that is empty.</summary>
    internal static string NamesChunk(IReadOnlyList<long> uuids)
    {
        var ids = new StringBuilder();
        for (var i = 0; i < uuids.Count; i++)
        {
            if (i > 0) ids.Append(',');
            ids.Append(uuids[i].ToString(CultureInfo.InvariantCulture));
        }
        return "local out = {} for _, id in ipairs({" + ids + "}) do " +
               "local ok, n = pcall(function() local e = Z.EntityMgr:GetEntity(id); if not e then return '' end; " +
               "local a = e:GetLuaAttr(Z.PbAttrEnum('AttrName')); local name = a and a.Value or ''; " +
               "if name == nil or name == '' then local c = e:GetLuaAttr(Z.PbAttrEnum('AttrId')); " +
               "local r = c and Z.TableMgr.GetTable('NpcTableMgr').GetRow(c.Value); name = r and r.Name or '' end; " +
               "return (string.gsub(tostring(name), '[\\t\\n]', ' ')) end); " +
               "out[#out + 1] = ok and n or '' end return table.concat(out, '\\n')";
    }

    /// <summary>"1" for a male model, "2" for a female one, "0" when unknown.</summary>
    internal static string GenderChunk(long uuid, bool self)
    {
        var read = self
            ? "local g = Z.ContainerMgr.CharSerialize.charBase.gender; "
            : "local e = Z.EntityMgr:GetEntity(" + uuid.ToString(CultureInfo.InvariantCulture) + "); if not e then return '0' end; " +
              "local a = e:GetLuaAttr(Z.PbAttrEnum('AttrGender')); local g = a and a.Value; ";
        return read + "if g == nil then return '0' end; if g == Z.PbEnum('EGender', 'GenderMale') then return '1' end; return '2'";
    }

    /// <summary>The NPC table <c>ModelID</c> of an NPC entity (run 5: AttrId → NpcTableMgr row), "0" when unknown.</summary>
    internal static string NpcModelChunk(long uuid) =>
        "local e = Z.EntityMgr:GetEntity(" + uuid.ToString(CultureInfo.InvariantCulture) + "); if not e then return '0' end; " +
        "local cfg = e:GetLuaAttr(Z.PbAttrEnum('AttrId')).Value; local r = Z.TableMgr.GetTable('NpcTableMgr').GetRow(cfg); " +
        "return tostring(r and r.ModelID or 0)";

    /// <summary>The game's own emote check (the photo panel's <c>checkEmoteCondition(id, true)</c> — shows the game's
    /// refusal tip itself). "true" when allowed.</summary>
    internal static string CheckChunk(int actionId)
    {
        var n = actionId.ToString(CultureInfo.InvariantCulture);
        return "local vm = Z.VMMgr.GetVM('expression'); if not vm then return 'novm' end; " +
               $"return tostring(vm.CheckEmoteCondition({n}, true) == true)";
    }

    internal static List<ExpressionInfo> ParseExpressions(string? answer)
    {
        var list = new List<ExpressionInfo>();
        if (Body(answer) is not { } text) return list;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            if (f.Length < 4 || !Int(f[0], out var id) || id <= 0 || !Int(f[2], out var male) || !Int(f[3], out var female)) continue;
            if (male <= 0 && female <= 0) continue;
            list.Add(new ExpressionInfo(id, f[1], male > 0 ? male : female, female > 0 ? female : male));
        }
        return list;
    }

    internal static Dictionary<long, string> ParseNames(string? answer, IReadOnlyList<long> uuids)
    {
        var lines = Body(answer)?.Split('\n') ?? Array.Empty<string>();
        var names = new Dictionary<long, string>(uuids.Count);
        for (var i = 0; i < uuids.Count; i++) names[uuids[i]] = i < lines.Length ? lines[i] : "";
        return names;
    }

    internal static int ParseGender(string? answer) => ParseInt(answer) == 2 ? 2 : 1;

    internal static int ParseInt(string? answer) => Body(answer) is { } b && Int(b.Trim(), out var v) ? v : 0;

    internal static bool ParseTrue(string? answer) => answer == "ok true";

    private static string? Body(string? answer) =>
        answer is not null && answer.StartsWith("ok ", StringComparison.Ordinal) ? answer.Substring(3) : null;

    private static bool Int(string s, out int value) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
