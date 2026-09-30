using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// Arbitrates <c>CameraFrameCtrl.SetEntityShow</c>, a switch our other-players / party hide SHARES with the game's own
/// camera mode (its UI toggles write the same per-type flags). The game exposes no getter, so the plan mirrors every
/// value the GAME writes (observed through a postfix; our own writes are excluded) and treats a never-written type as
/// shown (the game's default). Hiding moves a type away from that value; releasing restores it exactly. Pure — no
/// Unity/game types — so it is unit-tested.
/// </summary>
internal sealed class EntityShowPlan
{
    // E.CameraSystemShowEntityType / Panda.ZGame.ECamerasysShowEntityType: Team = 3, OtherPlayer = 11.
    public const int Team = 3;
    public const int OtherPlayer = 11;

    private readonly Dictionary<int, bool> _game = new();      // last value the game itself wrote
    private readonly Dictionary<int, bool> _current = new();   // last value on the switch (anyone)

    /// <summary>The game wrote <paramref name="show"/> for <paramref name="type"/> (not one of our own writes).</summary>
    public void ObserveGame(int type, bool show)
    {
        _game[type] = show;
        _current[type] = show;
    }

    /// <summary>
    /// Forgets the mirror (the game re-initialised CameraFrameCtrl and may have reset its flags to the defaults), so
    /// the next <see cref="Apply"/> re-writes the held state instead of trusting a stale "already hidden".
    /// </summary>
    public void Reset()
    {
        _game.Clear();
        _current.Clear();
    }

    /// <summary>True when <see cref="Apply"/> would write anything.</summary>
    public bool NeedsWrite(bool hideOthers, bool keepParty) =>
        Current(OtherPlayer) != Want(OtherPlayer, hideOthers, keepParty) || Current(Team) != Want(Team, hideOthers, keepParty);

    /// <summary>
    /// Moves the switch to the target state: others hidden (and the team too unless <paramref name="keepParty"/>),
    /// otherwise the game's own values. Only differing types are written; a failed write is retried next time.
    /// </summary>
    public bool Apply(bool hideOthers, bool keepParty, Func<int, bool, bool> write)
    {
        var ok = Reach(OtherPlayer, Want(OtherPlayer, hideOthers, keepParty), write);
        return Reach(Team, Want(Team, hideOthers, keepParty), write) && ok;
    }

    private bool Want(int type, bool hideOthers, bool keepParty) => type switch
    {
        OtherPlayer when hideOthers => false,
        Team when hideOthers && !keepParty => false,
        _ => Game(type),
    };

    private bool Game(int type) => !_game.TryGetValue(type, out var v) || v;

    private bool Current(int type) => _current.TryGetValue(type, out var v) ? v : Game(type);

    private bool Reach(int type, bool want, Func<int, bool, bool> write)
    {
        if (Current(type) == want) return true;
        if (!write(type, want)) return false;
        _current[type] = want;
        return true;
    }
}
