using System;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// Safety net for a photo copy the game half-made (regression id <c>clone-nre-male-null-ridetpl</c>, probe run 6): when
/// <c>CloneModelForPhoto</c> throws, <c>ZModelManager.cloneModel</c> has already registered the copy (modelDict_ +1,
/// never loaded, never drawn) and the caller never receives it. A postfix on <c>cloneModel</c> feeds
/// <see cref="Record"/>; it records only while <see cref="Run"/> is inside its own clone call on the arming thread. Unarmed,
/// <see cref="Record"/> is one field compare and allocates nothing of its own (the interop wrapper the game's return value
/// arrives in is made by the postfix trampoline before this runs — not ours to avoid). On a throw the recorded copy is
/// removed (isolated: a failing removal is warned, never hides the original failure) and the exception is rethrown
/// unchanged. Pure (unit-tested).
/// <para>Only the FIRST model is kept (review M-6). A source riding or seated on something is copied as 2 models, both
/// made inside the armed call (2026-10-02 sweeps: WhiteTea +2; fix2 runs Aster and Remmir — Male, on a throne —
/// <c>records=2</c>, modelDict_ +2), and <c>RecyclePhotoModel</c> on the returned copy removed BOTH every time (the count
/// came back to its pre-open value), so the copy is the one handle that matters. The throw this net exists for cannot
/// reach a 2-model copy anyway: it needs a NULL ride template, and every 2-model source measured carried a non-null one
/// (<c>rideTplNull=False</c>: probe run 6's mounted source, WhiteTea, Aster, Remmir), so the callback does not throw;
/// every null-template source measured was a 1-model copy (<c>records=1</c>).</para>
/// </summary>
internal sealed class CloneOrphanNet
{
    private int _armedThread;   // 0 = unarmed (managed thread ids start at 1)
    private object? _created;
    private int _records;

    public bool Armed => _armedThread != 0;

    /// <summary>How many models the postfix handed over during the LAST armed call (diagnostics; 0 before any call).</summary>
    public int LastRecords { get; private set; }

    /// <summary>The <c>cloneModel</c> postfix: keeps the FIRST model made during the armed clone call, counts all.</summary>
    public void Record(object? created)
    {
        if (_armedThread == 0 || created is null) return;
        if (Environment.CurrentManagedThreadId != _armedThread) return;
        _records++;
        _created ??= created;
    }

    /// <summary>Runs <paramref name="clone"/> armed. Success: its result, nothing removed. Throw: the copy the game
    /// registered (if one was recorded) goes to <paramref name="recycle"/> exactly once, then the exception rethrows.
    /// <paramref name="recycle"/> returns false when the game's removal call is unavailable.</summary>
    public object? Run(Func<object?> clone, Func<object, bool> recycle, Action<string> warn)
    {
        _created = null;
        _records = 0;
        _armedThread = Environment.CurrentManagedThreadId;
        try { return clone(); }
        catch
        {
            var orphan = _created;
            Disarm();
            if (orphan is not null) RemoveOrphan(orphan, recycle, warn);
            throw;
        }
        finally { Disarm(); }
    }

    private void Disarm()
    {
        if (_armedThread != 0) LastRecords = _records;
        _armedThread = 0;
        _created = null;
    }

    private static void RemoveOrphan(object orphan, Func<object, bool> recycle, Action<string> warn)
    {
        try
        {
            warn(recycle(orphan)
                ? "the game's photo copy failed half-way; removed the copy it had already made (recycled=True)"
                : "the game's photo copy failed half-way; the copy it had already made could NOT be removed " +
                  "(recycled=False: RecyclePhotoModel unavailable)");
        }
        catch (Exception ex)
        {
            warn($"removing the half-made photo copy failed: {(ex.InnerException ?? ex).Message}");
        }
    }
}
