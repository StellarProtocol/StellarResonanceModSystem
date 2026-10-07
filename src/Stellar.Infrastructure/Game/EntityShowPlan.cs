using System;
using System.Collections.Generic;
using System.Linq;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// Arbitrates <c>CameraFrameCtrl.SetEntityShow</c> for the other-players / keep-party hide. Recon (devkit
/// .superpowers/sdd/recon-party-grain.md, from the release_3.7 disassembly): each call moves a per-type
/// <b>refcount</b> in <c>ZEntityMgr</c> for the photo source (hide +1, show −1 clamped at 0) — shared with the game's
/// own camera Show panel — and <c>OtherPlayer</c> (11) is a master switch checked before every relation, so
/// <c>Team</c> (3) cannot rescue a teammate while 11 is hidden. Hence:
/// <list type="bullet">
/// <item>hide everyone: <c>11</c>;</item>
/// <item>hide others but keep the party: <c>Stranger 6 + Chum 2 + Union 4</c> (the game's own way: a character is
/// visible when any relation they belong to is shown; party members keep Team, which we never write);</item>
/// </list>
/// The plan keeps hide-once / show-once bookkeeping of only the types WE hid. The game's own writes are not mirrored —
/// the counter composes them for us — and a held type is never hidden twice (every extra hide would need a matching
/// show). The game can reset the counters under us; given the live count (<c>ZEntityMgr.getHideCount</c> for the photo
/// source, see <see cref="HideTypeFor"/>), a held type whose count reads 0 is re-hidden exactly once and gets no show
/// at release (a show there could cancel a hold someone took after the reset). An unreadable count (null) changes
/// nothing. Pure — no Unity/game types — so it is unit-tested. Since 2.20.0 one plan carries every world layer's types
/// (<see cref="EntityShowTargets"/>), so two layers sharing a type (Self and SelfCharacter both use Oneself) hold it once.
/// </summary>
internal sealed class EntityShowPlan
{
    // E.CameraSystemShowEntityType / Panda.ZGame.ECamerasysShowEntityType.
    public const int Chum = 2;
    public const int Union = 4;
    public const int Stranger = 6;
    public const int OtherPlayer = 11;
    // Panda.ZGame.ECamerasysShowEntityType — the local player and their pet/summon.
    public const int Oneself = 1;
    public const int SelfPet = 14;

    private static readonly int[] KeepPartySet = { Stranger, Chum, Union };
    private static readonly int[] EveryoneSet = { OtherPlayer };
    public static readonly int[] SelfSet = { Oneself, SelfPet };

    private readonly List<int> _held = new();   // types WE hid, in hide order

    /// <summary>True while the party-preserving set (6/2/4) is what we hold — party changes then need a refresh.</summary>
    public bool HoldsKeepPartySet => _held.Count > 0 && _held.All(t => Array.IndexOf(KeepPartySet, t) >= 0);

    /// <summary>True while anything is held — a type we hid and haven't shown back yet. M2 follow-up: a failed show
    /// stays held for retry (see <see cref="Apply(IReadOnlyList{int},Func{int,bool,bool},Func{int,int?})"/>'s doc),
    /// so this is exactly "a restore is still owed" for this plan even once its layer is no longer requested.</summary>
    public bool HoldsAny => _held.Count > 0;

    /// <summary>True while <paramref name="type"/> is one WE hid and haven't shown back.</summary>
    public bool Holds(int type) => _held.Contains(type);

    /// <summary>True while we hold a type <paramref name="target"/> no longer wants — a failed show-back still owed.</summary>
    public bool HoldsAnyOutside(IReadOnlyList<int> target) => _held.Any(t => !target.Contains(t));

    /// <summary>True while a player-group type (Stranger 6 / Chum 2 / Team 3 / Union 4) is held without the master
    /// switch 11: who is shown then depends on live relations (party, friends, guild), so a change needs a refresh.</summary>
    public bool HoldsRelationHide =>
        !_held.Contains(OtherPlayer) && _held.Any(t => t is Stranger or Chum or Union or EntityShowTargets.Team);

    /// <summary><c>EntityRenderLayerHideType</c> behind a camera type, or null when not measured (the hold-count check
    /// is then skipped for it — bookkeeping only).</summary>
    public static int? TryHideTypeFor(int cameraType) => cameraType switch
    {
        Stranger => 6,      // Nearby
        Chum => 5,          // Friend
        Union => 3,         // Union
        OtherPlayer => 7,   // OtherPlayer
        SelfPet => 12,      // SelfPet — measured 2026-10-03 (owner client: "holds after Self … SelfPet(12)=1")
        // Oneself (1) keeps no ETakePhotos counter (same measurement), so it stays unmapped: bookkeeping only.
        _ => null,
    };

    /// <summary>
    /// <c>Panda.ZGame.EntityRenderLayerHideType</c> that <c>CameraFrameCtrl.SetEntityShow(cameraType, …)</c> drives
    /// (decoded jump table, recon-party-grain.md; enum values verified in the release_3.7 interop).
    /// </summary>
    public static int HideTypeFor(int cameraType) =>
        TryHideTypeFor(cameraType) ?? throw new ArgumentOutOfRangeException(nameof(cameraType));

    /// <summary>True when <see cref="Apply(bool,bool,Func{int,bool,bool},Func{int,int?})"/> would write anything or drop a reset hold.</summary>
    public bool NeedsWrite(bool hideOthers, bool keepParty, Func<int, int?>? holdCount = null) =>
        NeedsWrite(Target(hideOthers, keepParty), holdCount);

    /// <summary>True when <see cref="Apply(IReadOnlyList{int},Func{int,bool,bool},Func{int,int?})"/> would write
    /// anything or drop a reset hold, for an arbitrary target set (e.g. <see cref="SelfSet"/>).</summary>
    public bool NeedsWrite(IReadOnlyList<int> target, Func<int, int?>? holdCount = null) =>
        _held.Count != target.Count || _held.Any(t => !target.Contains(t) || IsReset(t, holdCount));

    /// <summary>
    /// Moves to the target set: first one show for every held type the target drops, then one hide for every target
    /// type not yet held. A failed hide is not held (retried next time); a failed show stays held (retried next
    /// time, so a hide is never stranded). Returns false when any write failed.
    /// </summary>
    public bool Apply(bool hideOthers, bool keepParty, Func<int, bool, bool> write, Func<int, int?>? holdCount = null) =>
        Apply(Target(hideOthers, keepParty), write, holdCount);

    /// <summary>
    /// Moves to an arbitrary target set (e.g. <see cref="SelfSet"/>) with the same hide-once / show-once /
    /// never-strand-a-hide semantics as the bool overload.
    /// </summary>
    public bool Apply(IReadOnlyList<int> target, Func<int, bool, bool> write, Func<int, int?>? holdCount = null)
    {
        var ok = true;
        _held.RemoveAll(t => IsReset(t, holdCount));   // our hold is gone: re-hide (if wanted) / no show (if not)
        foreach (var type in _held.Where(t => !target.Contains(t)).ToList())
        {
            if (write(type, true)) _held.Remove(type);
            else ok = false;
        }
        foreach (var type in target)
        {
            if (_held.Contains(type)) continue;
            if (write(type, false)) _held.Add(type);
            else ok = false;
        }
        return ok;
    }

    private static bool IsReset(int type, Func<int, int?>? holdCount) => holdCount?.Invoke(type) == 0;

    private static int[] Target(bool hideOthers, bool keepParty) =>
        !hideOthers ? Array.Empty<int>() : keepParty ? KeepPartySet : EveryoneSet;
}
