using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Stellar.Abstractions.Services;
using ZstdSharp;

namespace Stellar.Application.Services;

/// <summary>One row of <see cref="GameTextTable"/>. Empty source fields are stored as null.</summary>
/// <param name="Name">English localized name, null for an unnamed (internal) row.</param>
/// <param name="Design">English design label (<c>NameDesign</c>).</param>
/// <param name="Description">English description, else (buffs) the English design note.</param>
internal sealed record GameTextRow(string? Name, string? Design, string? Description);

/// <summary>
/// The parsed English game-text table (embedded <c>GameText.en.tsv.zst</c>, built by
/// <c>tools/gametext/build_gametext.py</c>). Immutable once built — <see cref="GameTextService"/> publishes it in
/// one volatile write, so readers on any thread see either nothing or the whole table.
/// </summary>
internal sealed class GameTextTable
{
    /// <summary>Manifest name of the embedded table (LogicalName in Stellar.Application.csproj).</summary>
    internal const string ResourceName = "Stellar.Application.GameText.en.tsv.zst";
    private const string Header = "STGT1";

    internal static readonly GameTextTable Empty = new(new Dictionary<int, GameTextRow>(), new Dictionary<int, GameTextRow>());

    private readonly Dictionary<int, GameTextRow> _buffs;
    private readonly Dictionary<int, GameTextRow> _skills;

    private GameTextTable(Dictionary<int, GameTextRow> buffs, Dictionary<int, GameTextRow> skills)
    {
        _buffs = buffs;
        _skills = skills;
    }

    internal int BuffCount => _buffs.Count;
    internal int SkillCount => _skills.Count;

    internal GameTextRow? Get(GameTextKind kind, int id)
    {
        var map = kind == GameTextKind.Skill ? _skills : _buffs;
        return map.TryGetValue(id, out var row) ? row : null;
    }

    /// <summary>Loads the table embedded in this assembly. Throws when the resource is missing or malformed.</summary>
    internal static GameTextTable LoadEmbedded()
    {
        using var s = typeof(GameTextTable).Assembly.GetManifestResourceStream(ResourceName)
                      ?? throw new InvalidDataException($"resource {ResourceName} not embedded");
        return Parse(s);
    }

    /// <summary>Parses a zstd-compressed table stream (format in <c>tools/gametext/build_gametext.py</c>).</summary>
    internal static GameTextTable Parse(Stream compressed)
    {
        using var z = new DecompressionStream(compressed);
        using var r = new StreamReader(z, Encoding.UTF8);
        if (r.ReadLine() != Header) throw new InvalidDataException("not a STGT1 game-text table");
        var buffs = new Dictionary<int, GameTextRow>(10240);
        var skills = new Dictionary<int, GameTextRow>(5120);
        var f = new string[6];
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0 || Split(line, f) < 6) continue;
            if (!int.TryParse(f[1], out var id)) continue;
            var map = f[0] == "S" ? skills : f[0] == "B" ? buffs : null;
            if (map == null) continue;
            var desc = Field(f[4]) ?? Field(f[5]);
            map[id] = new GameTextRow(Field(f[2]), Field(f[3]), desc);
        }
        return new GameTextTable(buffs, skills);
    }

    // Tab split into a reused array; returns the field count (extra fields are ignored).
    private static int Split(string line, string[] into)
    {
        int n = 0, start = 0;
        for (int i = 0; i <= line.Length && n < into.Length; i++)
        {
            if (i < line.Length && line[i] != '\t') continue;
            into[n++] = line.Substring(start, i - start);
            start = i + 1;
        }
        return n;
    }

    private static string? Field(string raw) => raw.Length == 0 ? null : Unescape(raw);

    // Reverses the generator's escaping: \\ \t \n \r. Most fields contain no backslash → returned as-is.
    private static string Unescape(string raw)
    {
        if (raw.IndexOf('\\') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length) { sb.Append(c); continue; }
            char e = raw[++i];
            sb.Append(e switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => e });
        }
        return sb.ToString();
    }
}
