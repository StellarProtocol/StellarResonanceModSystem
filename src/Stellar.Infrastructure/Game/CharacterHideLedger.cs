using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// Which characters the framework itself hid on one per-character channel (body or weapon), so a release shows back
/// exactly those and nobody else. Both game writes behind it are per-source BITS, not counters
/// (<c>ZEntityHelper.setVisible</c> bts/btr; <c>RenderCompBase.SetDisappear</c> per source), so a wanted hide is always
/// re-issued — idempotent, and it re-applies after the game rebuilt the model — while a show is issued only for a
/// character this ledger holds. A failed hide is not held; a failed show stays held so the next pass retries it.
/// Pure — unit-tested. Main thread.
/// </summary>
internal sealed class CharacterHideLedger
{
    private readonly HashSet<long> _held = new();
    private readonly List<long> _scratch = new();

    /// <summary>Characters currently held hidden.</summary>
    public int Count => _held.Count;

    /// <summary>True while <paramref name="uuid"/> is one this ledger hid and has not shown back.</summary>
    public bool Holds(long uuid) => _held.Contains(uuid);

    /// <summary>The write a character needs: true = hide (always, when wanted), false = show (only one we hid), null =
    /// nothing. Records the hide/show as done; report a failed write with <see cref="Failed"/>.</summary>
    public bool? Step(long uuid, bool want)
    {
        if (want)
        {
            _held.Add(uuid);
            return true;
        }
        return _held.Remove(uuid) ? false : null;
    }

    /// <summary>Undoes the bookkeeping of a write that failed: a hide is not held, a show stays held (retried).</summary>
    public void Failed(long uuid, bool hide)
    {
        if (hide) _held.Remove(uuid);
        else _held.Add(uuid);
    }

    /// <summary>Forgets every held character that is no longer present (left view / scene change): there is nothing
    /// left to show back, and a new character reusing the id starts clean.</summary>
    public void Prune(HashSet<long> present)
    {
        _scratch.Clear();
        foreach (var uuid in _held)
            if (!present.Contains(uuid)) _scratch.Add(uuid);
        foreach (var uuid in _scratch) _held.Remove(uuid);
    }
}
