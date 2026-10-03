using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

/// <summary>The Lua chunks <see cref="EmoteService"/> runs and the parsers for their answers. The calls are the game's
/// own emote-wheel path (expression_vm.lua: GetExpressionShowDataByType / CheckEmoteCondition / PlayAction).</summary>
internal static class EmoteLua
{
    /// <summary>Runs <paramref name="body"/> under pcall and stores <c>"ok &lt;result&gt;"</c> or <c>"err &lt;message&gt;"</c>.</summary>
    internal static string Wrap(string body) =>
        "rawset(_G,'" + EmoteService.OutGlobal + "',nil); local _ok, _r = pcall(function() " + body + " end); " +
        "rawset(_G,'" + EmoteService.OutGlobal + "', _ok and ('ok ' .. tostring(_r)) or ('err ' .. tostring(_r)))";

    /// <summary>One line per unlocked single-player action: id TAB name TAB icon TAB looping(1/0).</summary>
    internal const string ListChunk = @"
local vm = Z.VMMgr.GetVM('expression')
if not vm then return '' end
local out, seen = {}, {}
for _, dt in ipairs({1, 2, 5}) do
  local list = vm.GetExpressionShowDataByType(dt, false, nil, true) or {}
  for _, d in ipairs(list) do
    local r = d.tableData
    if r and r.Type == 1 and d.activeType == 1 and not seen[r.Id] then
      seen[r.Id] = true
      local name = (string.gsub(tostring(r.Name or ''), '[\t\n]', ' '))
      out[#out + 1] = tostring(r.Id) .. '\t' .. name .. '\t' .. tostring(r.Icon or '') .. '\t' .. (dt == 2 and '1' or '0')
    end
  end
end
return table.concat(out, '\n')";

    internal static string PlayChunk(int id)
    {
        var n = id.ToString(CultureInfo.InvariantCulture);
        return "local vm = Z.VMMgr.GetVM('expression'); if not vm then return 'novm' end; " +
               $"if not vm.CheckEmoteCondition({n}, true) then return 'refused' end; " +
               $"vm.PlayAction({n}, true, false); return 'played'";
    }

    internal static EmoteResult ParsePlay(string? answer) => answer switch
    {
        "ok played" => EmoteResult.Played,
        "ok refused" => EmoteResult.Refused,
        "ok novm" => EmoteResult.Unavailable,
        _ => EmoteResult.Failed,
    };

    internal static List<EmoteInfo> ParseList(string text)
    {
        var list = new List<EmoteInfo>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            if (f.Length < 4 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            list.Add(new EmoteInfo(id, f[1], f[2], f[3] == "1"));
        }
        return list;
    }
}
