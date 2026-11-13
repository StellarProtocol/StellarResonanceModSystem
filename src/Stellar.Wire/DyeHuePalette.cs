using System;

namespace Stellar.Wire;

/// <summary>Maps a dye triple's wire <c>x</c> to a hue in degrees for one cosmetic. Supplied by the layer
/// that can read game tables (Infrastructure); <see cref="AttrFashionDataReader"/> stays a pure decoder.
/// Must be cheap and allocation-free on a cache hit — it runs once per dyed area per decode.</summary>
public delegate int DyeHueMapper(int fashionId, int x);

/// <summary>How the game turns a dye's wire <c>x</c> into a hue, for one <c>ColorGroupTable</c> row
/// (the piece's <c>FashionTable.ColorGroupId</c>). Truth: <c>fashion_data.lua</c>
/// <c>convertProtoVectorToHSV</c> (release_3.7, <c>fashion_data.lua:327-351</c>), encode side <c>fashion_vm.lua:897-930</c>,
/// <c>fashion_save_tips_vm.lua:103-118</c> (hue → id, the clean inverse):
/// <list type="bullet">
/// <item><c>Type == 3</c> (<c>EHueModifiedMode.Board</c>) — <c>x</c> IS the hue in degrees.</item>
/// <item>any other type (<c>Option = 1</c>, <c>Slider = 2</c>) — <c>x</c> is the swatch ID: the group's
/// <c>Hue</c> column is <c>[[id, hueDegrees], …]</c> and the client picks the row whose FIRST element equals
/// <c>x</c> (an ID match, 1-based in every 3.7 row — not a positional index). <c>x == 0</c> or no matching
/// row → hue 0.</item>
/// <item>no group row (e.g. <c>ColorGroupId == 0</c>) or no fashion row → hue 0 (the Lua's <c>h</c> starts at
/// 0 and is only set inside the row branches).</item>
/// </list>
/// Saturation / value (<c>y</c> / <c>z</c>) are passed through unchanged in every mode.
/// Immutable; safe to share across threads.</summary>
public sealed class DyeHuePalette
{
    private readonly int[] _ids;
    private readonly int[] _hues;
    private readonly bool _board;

    private DyeHuePalette(bool board, int[] ids, int[] hues)
    {
        _board = board;
        _ids = ids;
        _hues = hues;
    }

    /// <summary><c>EHueModifiedMode.Board</c> — the wire value is already degrees.</summary>
    public const int BoardType = 3;

    /// <summary>Board group: <c>x</c> passes through as degrees.</summary>
    public static readonly DyeHuePalette Board = new(true, Array.Empty<int>(), Array.Empty<int>());

    /// <summary>No colour-group row: the game decodes every hue as 0.</summary>
    public static readonly DyeHuePalette NoGroup = new(false, Array.Empty<int>(), Array.Empty<int>());

    /// <summary>Build from a group row. <paramref name="hueRows"/> is the <c>Hue</c> column
    /// (<c>[id, degrees]</c> pairs); rows shorter than 2 are skipped, as the Lua's nil check does.</summary>
    public static DyeHuePalette FromGroup(int type, int[][] hueRows)
    {
        if (type == BoardType) return Board;
        int n = 0;
        foreach (var r in hueRows) if (r is { Length: >= 2 }) n++;
        var ids = new int[n];
        var hues = new int[n];
        int i = 0;
        foreach (var r in hueRows)
        {
            if (r is not { Length: >= 2 }) continue;
            ids[i] = r[0];
            hues[i] = r[1];
            i++;
        }
        return new DyeHuePalette(false, ids, hues);
    }

    /// <summary>Hue in degrees for wire value <paramref name="x"/>. First matching ID wins (the Lua breaks
    /// on the first hit).</summary>
    public int ToDegrees(int x)
    {
        if (_board) return x;
        if (x == 0) return 0;
        for (int i = 0; i < _ids.Length; i++)
            if (_ids[i] == x) return _hues[i];
        return 0;
    }
}
