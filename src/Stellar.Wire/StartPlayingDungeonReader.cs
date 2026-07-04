using System;

namespace Stellar.Wire;

/// <summary>
/// Minimal structural parser for <c>WorldNtf.NotifyStartPlayingDungeon</c>
/// (method 55) — the play-start EDGE notification. The consumer only needs the
/// packet's ARRIVAL (the arrival instant IS the run-timer start); the
/// <c>char_id</c> decoded here is diagnostic-only (the game's own Lua handler,
/// <c>world_ntf_impl.lua NotifyStartPlayingDungeon</c>, early-returns on the
/// local player's own char_id — i.e. the ntf also fires for OTHER party
/// members' starts, so duplicates per run are expected and the latch guard
/// makes them harmless).
///
/// <para>
/// Wire layout (per <c>proto/zproto/serv_world_ntf.proto</c> +
/// <c>stru_start_playing_dungeon_param.proto</c>):
/// <code>
/// message NotifyStartPlayingDungeon { StartPlayingDungeonParam v_param = 1; }
/// message StartPlayingDungeonParam  { int64 char_id = 1; bool is_use_key = 2; }
/// </code>
/// </para>
///
/// <para>
/// BCL-only, side-effect-free and fully defensive (mirrors
/// <see cref="DungeonSyncReader"/>) — malformed input short-circuits to
/// <see langword="false"/>, never an exception.
/// </para>
/// </summary>
public static class StartPlayingDungeonReader
{
    /// <summary>
    /// Attempt to decode <paramref name="worldNtfBody"/> as a
    /// <c>NotifyStartPlayingDungeon</c> packet and extract
    /// <c>v_param.char_id</c>. Returns <see langword="true"/> on a structurally
    /// valid parse; <paramref name="charId"/> is 0 when the field is absent.
    /// </summary>
    public static bool TryReadCharId(ReadOnlySpan<byte> worldNtfBody, out long charId)
    {
        charId = 0;

        // Outer envelope: NotifyStartPlayingDungeon { StartPlayingDungeonParam v_param = 1 }.
        if (!WireProtocol.TryReadVRequest(worldNtfBody, out var param))
            return false;

        int pos = 0;
        while (pos < param.Length)
        {
            if (!WireProtocol.TryReadTag(param, ref pos, out var field, out var wire))
                return false;

            if (field == 1 && wire == 0)
            {
                // char_id (int64 varint).
                if (!WireProtocol.TryReadVarint(param, ref pos, out var v)) return false;
                charId = (long)v;
                return true;
            }

            if (!WireProtocol.SkipField(param, ref pos, wire))
                return false;
        }

        // Structurally valid but no char_id field — acceptable (diag-only value).
        return true;
    }
}
