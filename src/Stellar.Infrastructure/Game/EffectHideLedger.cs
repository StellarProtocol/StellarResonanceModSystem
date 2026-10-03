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

    public bool ShouldHide(long uid, VisibilityLayers owner, VisibilityLayers wanted, bool currentlyVisible) =>
        uid != 0 && owner != VisibilityLayers.None && (wanted & owner) != 0 && currentlyVisible && !_hidden.ContainsKey(uid);

    public void MarkHidden(long uid, VisibilityLayers owner)
    {
        if (uid != 0) _hidden[uid] = owner;
    }

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
