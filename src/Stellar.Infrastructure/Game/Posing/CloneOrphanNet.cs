using System;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// Safety net for a photo copy the game half-made (regression id <c>clone-nre-male-null-ridetpl</c>, probe run 6): when
/// <c>CloneModelForPhoto</c> throws, <c>ZModelManager.cloneModel</c> has already registered the copy (modelDict_ +1,
/// never loaded, never drawn) and the caller never receives it. A postfix on <c>cloneModel</c> feeds
/// <see cref="Record"/>; it records only while <see cref="Run"/> is inside its own clone call on the arming thread, so an
/// unarmed call is one field compare and no allocation. On a throw the recorded copy is removed (isolated: a failing
/// removal is warned, never hides the original failure) and the exception is rethrown unchanged. Pure (unit-tested).
/// </summary>
internal sealed class CloneOrphanNet
{
    private int _armedThread;   // 0 = unarmed (managed thread ids start at 1)
    private object? _created;

    public bool Armed => _armedThread != 0;

    /// <summary>The <c>cloneModel</c> postfix: keeps the FIRST model made during the armed clone call.</summary>
    public void Record(object? created)
    {
        if (_armedThread == 0 || created is null || _created is not null) return;
        if (Environment.CurrentManagedThreadId != _armedThread) return;
        _created = created;
    }

    /// <summary>Runs <paramref name="clone"/> armed. Success: its result, nothing removed. Throw: the copy the game
    /// registered (if one was recorded) goes to <paramref name="recycle"/> exactly once, then the exception rethrows.</summary>
    public object? Run(Func<object?> clone, Action<object> recycle, Action<string> warn)
    {
        _created = null;
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
        _armedThread = 0;
        _created = null;
    }

    private static void RemoveOrphan(object orphan, Action<object> recycle, Action<string> warn)
    {
        try
        {
            recycle(orphan);
            warn("the game's photo copy failed half-way; removed the copy it had already made");
        }
        catch (Exception ex)
        {
            warn($"removing the half-made photo copy failed: {(ex.InnerException ?? ex).Message}");
        }
    }
}
