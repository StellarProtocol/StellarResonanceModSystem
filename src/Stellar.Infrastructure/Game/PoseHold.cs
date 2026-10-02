using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>The position hold's book (recon run 2 C W2, run 3 R3-9) — now position AND ROTATION (owner MAIN evidence
/// 2026-10-02: a frozen boss kept turning; <c>MoveComp.RotGo / SimpleRotGo</c> kept writing the drawn rotation and nothing
/// held it). Each held entity keeps the pose it was DRAWN at when the hold took it; <see cref="Tick"/> writes that pose
/// back every late frame (the sanctioned per-frame hold write), position always and rotation whenever it was readable at
/// hold time. <see cref="Release"/> snaps each held model to its LOGICAL pose — the logical position always, the logical
/// rotation when the game serves one (else the drawn rotation is left to the game) — so nothing stays where the freeze left
/// it. Every entity's write runs in its own try: one bad entity never skips the rest. Generic over the pose types so it is
/// pure (unit-tested with plain structs; the backend uses Unity's <c>Vector3</c> / <c>Quaternion</c>).</summary>
internal sealed class PoseHold<TPos, TRot> where TPos : struct where TRot : struct
{
    /// <summary>One held entity: its uuid and the pose it was drawn at (rotation null = unreadable, never written).</summary>
    internal readonly record struct Entry(long Uuid, TPos Pos, TRot? Rot);

    private readonly List<Entry> _held = new();

    public int Count => _held.Count;

    public IReadOnlyList<Entry> Entries => _held;

    public bool Contains(long uuid) => IndexOf(uuid) >= 0;

    /// <summary>The held entry of <paramref name="uuid"/>, or null.</summary>
    public Entry? Find(long uuid) => IndexOf(uuid) is >= 0 and var i ? _held[i] : null;

    public void Add(long uuid, TPos pos, TRot? rot) => _held.Add(new Entry(uuid, pos, rot));

    public bool Remove(long uuid)
    {
        var i = IndexOf(uuid);
        if (i < 0) return false;
        _held.RemoveAt(i);
        return true;
    }

    public void Clear() => _held.Clear();

    /// <summary>One late frame: every held entity whose drawn component <paramref name="comp"/> still serves gets its held
    /// position, and its held rotation when it has one. A throw is the caller's (the hold stops as a whole).</summary>
    public void Tick(Func<long, object?> comp, Action<object, TPos> setPos, Action<object, TRot>? setRot)
    {
        foreach (var h in _held)
        {
            if (comp(h.Uuid) is not { } c) continue;
            setPos(c, h.Pos);
            if (setRot is not null && h.Rot is { } r) setRot(c, r);
        }
    }

    /// <summary>Snaps every held entity to its logical pose (<paramref name="logical"/>: the live drawn component and the
    /// logical position / rotation, or null when gone). Isolated per entity; <paramref name="onError"/> hears a throw.</summary>
    public void Release(Func<long, (object Comp, TPos Pos, TRot? Rot)?> logical, Action<object, TPos> setPos,
        Action<object, TRot>? setRot, Action<Exception> onError)
    {
        foreach (var h in _held) Snap(h.Uuid, logical, setPos, setRot, onError);
    }

    /// <summary>Snaps one entity to its logical pose (see <see cref="Release"/>).</summary>
    public static void Snap(long uuid, Func<long, (object Comp, TPos Pos, TRot? Rot)?> logical, Action<object, TPos> setPos,
        Action<object, TRot>? setRot, Action<Exception> onError)
    {
        try
        {
            if (logical(uuid) is not { } l) return;
            setPos(l.Comp, l.Pos);
            if (setRot is not null && l.Rot is { } r) setRot(l.Comp, r);
        }
        catch (Exception ex) { onError(ex); }
    }

    private int IndexOf(long uuid)
    {
        for (var i = 0; i < _held.Count; i++)
            if (_held[i].Uuid == uuid) return i;
        return -1;
    }
}
