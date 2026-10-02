using System;
using System.Collections.Generic;
using System.Threading;
namespace Stellar.Infrastructure.Game;

/// <summary>What one combat-freeze evidence counter counts. The set_Speed gate's own decisions come first, then the
/// candidate alternative drivers hooked in diagnostics mode only (release_3.7 interop, decompiled with ilspycmd).</summary>
internal enum DiagSlot
{
    /// <summary><c>AnimCompBase.set_Speed</c>: the gate wrote 0 instead.</summary>
    GateSub,
    /// <summary>set_Speed passed through: the component is not tracked by the gate.</summary>
    GatePassUntracked,
    /// <summary>set_Speed passed through: the entity is excluded (self / own mount).</summary>
    GatePassExcluded,
    /// <summary>set_Speed passed through: our own write.</summary>
    GatePassOwn,
    /// <summary><c>ECSAnimController.set_Speed</c> / <c>ZAnimController.set_Speed</c> — the controller BEHIND the drawn
    /// speed, written directly (6 / 9 native callers), bypassing the gated setter.</summary>
    CtlSpeed,
    /// <summary>The same with a value above 0 — the controller was set running.</summary>
    CtlSpeedUp,
    /// <summary><c>ZAnimController.set_PauseGraph</c> (GameObject models).</summary>
    CtlPause,
    /// <summary><c>AnimCompBase.PlayBaseState / PlayUpperState / PlayManualClip</c>.</summary>
    AnimPlay,
    /// <summary><c>ECSAnimController.playECSState</c> (the ECS state switch every Play* ends in).</summary>
    CtlPlay,
    /// <summary><c>ZSkillShow.PlaySkillStage</c> / <c>PlayVehicleShow</c>, by the caster uuid argument.</summary>
    SkillStage,
    /// <summary><c>ZStateSkillComp.Update / motionUpdate</c> — the skill state's per-frame tick (skill displacement).</summary>
    SkillTick,
    /// <summary><c>ZStateSkillComp.doSkillStage / stageInitAnim / correctPosition</c>.</summary>
    SkillStep,
    /// <summary><c>ZStateMoveComp.Update</c> — the move state's per-frame tick.</summary>
    MoveTick,
    /// <summary><c>MoveComp.SimpleMoveGo / RotGo / SimpleRotGo</c> (the by-ref <c>MoveGo*</c> overloads are never hooked) — writes of the drawn ("Go") transform.</summary>
    MoveGo,
    /// <summary>A game write of the drawn position (<c>ECSModelGoComp / ModelGoComp.set_Position</c>) BEFORE our hold
    /// write this frame (the hold overwrites it).</summary>
    GoPosBefore,
    /// <summary>A game write of the drawn position AFTER our hold write this frame — what renders is off the hold.</summary>
    GoPosAfter,
    /// <summary><c>AddEffectDisplay(ZEffect)</c> while frozen (the hooked creation path), by the effect's belong uuid.</summary>
    FxDisplay,
    /// <summary><c>AddEffectDisplay(EffectBinder_Runtime)</c> — the overload the freeze ignores.</summary>
    FxBinder,
    /// <summary><c>ZEffect.Init(EffectContext)</c> — every effect initialised while frozen.</summary>
    FxInit,
    /// <summary><c>ZEffectManager.AddEffect</c> (3 overloads).</summary>
    FxAdd,
    /// <summary>A game call un-freezing an effect (<c>SetEffectFreeze(…, false)</c>) while frozen.</summary>
    FxUnfreeze,
    /// <summary><c>ZEffectManager.SetEffectSpeed / SetEffectSpeedScale</c> while frozen.</summary>
    FxSpeed,
}

/// <summary>Hit counters for the combat-freeze evidence capture (diagnostics only). Every counter is either per WATCHED
/// entity (the freeze's monsters / bosses, capped at <see cref="MaxWatched"/>) or global. A hook names its entity by a
/// native pointer — an anim component, controller, drawn-transform component or game component — mapped to its owner's
/// uuid by the sampler (<see cref="Map"/>, capped at <see cref="MaxPointers"/>; never dereferenced). Constructed with
/// <c>enabled = StellarDiagnostics.IsEnabled</c>: when not enabled every call is a no-op (inert). Main thread only, except
/// <see cref="OffThread"/> (atomic). Pure (unit-tested).</summary>
internal sealed class FreezeDiagCounters
{
    internal const int MaxWatched = 64;
    internal const int MaxPointers = 4096;
    internal static readonly int SlotCount = Enum.GetValues(typeof(DiagSlot)).Length;

    private readonly bool _enabled;
    private readonly Dictionary<long, int[]> _total = new();
    private readonly Dictionary<long, int[]> _sinceTake = new();
    private readonly Dictionary<IntPtr, long> _owner = new();
    private readonly int[] _global = new int[SlotCount];
    private readonly int[] _globalWatched = new int[SlotCount];
    private int _offThread;

    public FreezeDiagCounters(bool enabled) => _enabled = enabled;

    public int Watched => _total.Count;
    public int Pointers => _owner.Count;
    /// <summary>Hook entries seen off the main thread (never counted per entity: no shared table is touched there).</summary>
    public int OffThreadHits => Volatile.Read(ref _offThread);

    public void Reset()
    {
        _total.Clear();
        _sinceTake.Clear();
        _owner.Clear();
        Array.Clear(_global, 0, _global.Length);
        Array.Clear(_globalWatched, 0, _globalWatched.Length);
        Interlocked.Exchange(ref _offThread, 0);
    }

    /// <summary>Starts counting <paramref name="uuid"/> per entity. False when not enabled, 0, or the cap is reached.</summary>
    public bool Watch(long uuid)
    {
        if (!_enabled || uuid == 0) return false;
        if (_total.ContainsKey(uuid)) return true;
        if (_total.Count >= MaxWatched) return false;
        _total[uuid] = new int[SlotCount];
        _sinceTake[uuid] = new int[SlotCount];
        return true;
    }

    public bool IsWatched(long uuid) => _total.ContainsKey(uuid);

    /// <summary>Names <paramref name="ptr"/>'s owner uuid (0 = resolved, but not an entity: no re-resolve).</summary>
    public void Map(IntPtr ptr, long uuid)
    {
        if (!_enabled || ptr == IntPtr.Zero) return;
        if (_owner.Count >= MaxPointers && !_owner.ContainsKey(ptr)) return;
        _owner[ptr] = uuid;
    }

    /// <summary>The owner mapped for <paramref name="ptr"/>; false when never mapped.</summary>
    public bool TryOwner(IntPtr ptr, out long uuid) => _owner.TryGetValue(ptr, out uuid);

    /// <summary>One hit on <paramref name="slot"/> for the entity behind <paramref name="ptr"/> (global when unmapped).</summary>
    public void HitPtr(IntPtr ptr, DiagSlot slot) => Hit(_owner.TryGetValue(ptr, out var u) ? u : 0, slot);

    /// <summary>One hit on <paramref name="slot"/>: global always, per entity when <paramref name="uuid"/> is watched.</summary>
    public void Hit(long uuid, DiagSlot slot)
    {
        if (!_enabled) return;
        var i = (int)slot;
        _global[i]++;
        if (uuid == 0 || !_total.TryGetValue(uuid, out var t)) return;
        t[i]++;
        _sinceTake[uuid][i]++;
        _globalWatched[i]++;
    }

    public void OffThread()
    {
        if (_enabled) Interlocked.Increment(ref _offThread);
    }

    public int Global(DiagSlot slot) => _global[(int)slot];

    /// <summary>Hits on <paramref name="slot"/> that named a watched entity.</summary>
    public int GlobalWatched(DiagSlot slot) => _globalWatched[(int)slot];

    public int Total(long uuid, DiagSlot slot) => _total.TryGetValue(uuid, out var t) ? t[(int)slot] : 0;

    /// <summary>Copies <paramref name="uuid"/>'s hits since the previous take into <paramref name="into"/> and restarts
    /// that window. False (and <paramref name="into"/> zeroed) when not watched.</summary>
    public bool Take(long uuid, int[] into)
    {
        Array.Clear(into, 0, into.Length);
        if (!_sinceTake.TryGetValue(uuid, out var s)) return false;
        Array.Copy(s, into, Math.Min(s.Length, into.Length));
        Array.Clear(s, 0, s.Length);
        return true;
    }
}
