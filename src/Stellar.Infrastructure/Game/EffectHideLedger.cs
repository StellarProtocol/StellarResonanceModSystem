using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

/// <summary>The effects Photo Studio hid, with the group each was hidden for. We only ever show what is recorded here
/// (an effect the game had hidden is never recorded, so never shown). Pure — unit-tested.</summary>
internal sealed class EffectHideLedger
{
    private readonly Dictionary<long, VisibilityLayers> _hidden = new();

    public int Count => _hidden.Count;
    public bool Contains(long uid) => _hidden.ContainsKey(uid);
    public bool TryGetOwner(long uid, out VisibilityLayers owner) => _hidden.TryGetValue(uid, out owner);

    public bool ShouldHide(long uid, VisibilityLayers owner, VisibilityLayers wanted, bool currentlyVisible) =>
        uid != 0 && owner != VisibilityLayers.None && (wanted & owner) != 0 && currentlyVisible && !_hidden.ContainsKey(uid);

    /// <summary>M1: whether an already-held uid should be re-hidden because the game showed it again (e.g. a
    /// cutscene ending resets effect visibility) while we still intend it hidden. Kept separate from
    /// <see cref="ShouldHide"/> rather than relaxing its "not already ours" clause — the two callers read a
    /// different <paramref name="held"/> bit (Sweep already knows <see cref="Contains"/> before calling this).</summary>
    public static bool ShouldReHide(bool held, bool visible) => held && visible;

    public void MarkHidden(long uid, VisibilityLayers owner)
    {
        if (uid != 0) _hidden[uid] = owner;
    }

    /// <summary>Drops a recorded uid without showing it back — used once an effect is known ended (I1's bounded
    /// instance prune), so the ledger and the `_instances` cache stay in sync.</summary>
    public bool Remove(long uid) => _hidden.Remove(uid);

    /// <summary>Removes and returns the recorded uids to show again: still alive and no longer wanted. Ended effects
    /// (<paramref name="isAlive"/> false) are dropped without a show.</summary>
    public List<long> TakeReleasable(VisibilityLayers wanted, Func<long, bool> isAlive)
    {
        var show = new List<long>();
        foreach (var (uid, owner) in _hidden.ToList())
        {
            if (!isAlive(uid)) { _hidden.Remove(uid); continue; }
            if ((wanted & owner) != 0) continue;
            _hidden.Remove(uid);
            show.Add(uid);
        }
        return show;
    }

    public void Clear() => _hidden.Clear();
}
