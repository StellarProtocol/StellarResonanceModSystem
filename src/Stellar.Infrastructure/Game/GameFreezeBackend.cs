using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
using Stellar.Infrastructure.Unity;
namespace Stellar.Infrastructure.Game;

// The time pause: GameClockPause.cs. Position + rotation hold: .Hold.cs. Deaths deferred until unfreeze: .Removal.cs.
// Animation requests held until unfreeze: .Anim.cs.
// The unfreeze order: FreezeTeardown.cs (+ .Teardown.cs). Logging: .Diagnostics.cs.

/// <summary>The game side of <c>ISceneFreeze</c> — a GLOBAL TIME PAUSE (Photo Studio scene-stays spec, amendment 2026-10-02
/// late; devkit recon <c>free-camera-recon.md</c> § Run 9). The game's update loop, skill timelines, effects, animation and
/// the local player all run on Unity's scaled time, so <see cref="GameClockPause"/> (<c>Time.timeScale = 0</c>, held against
/// the game's own writes) stops them all at once. Two things a pause alone does not stop are kept from the former
/// per-entity freeze: remote players and monsters still MOVE, because move packets write their drawn position directly
/// (R9-2) — the position hold pins every movable entity but the local player and their own mount (<see cref="FreezeTargets"/>)
/// to where it was drawn at the press; and a monster killed while frozen stays visible until the freeze ends (the deferred
/// removal). One thing the pause does not stop is new: the game keeps REQUESTING animation states for movers (run 11), so
/// those requests are held until unfreeze (<see cref="AnimRequestGate"/>); the local player's own movement and combat input
/// is masked while paused (Host: the pause's input block). Each part fails open on its own with one warning; the clock is resumed whatever else fails. One LateUpdate
/// handler runs the hold; the frame driver is off whenever it is not live. Main thread.</summary>
internal sealed partial class GameFreezeBackend : ISceneFreezeBackend, IFreezeTeardownSteps
{
    private const string Tag = "[FreeCam] ";

    private readonly GameEntityAccess _entities;
    private readonly FrameDriverHost _driver;
    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;
    private readonly GameClockPause _clock;
    private readonly FreezeLedger _ledger = new();
    private readonly LazyHookInstall _hooks = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly List<long> _ids = new();
    private readonly Action _lateTick;
    private readonly Action _flushOnUnfreeze;
    private bool _frozen;
    private bool _lateOn;
    private int _mainThread;

    public GameFreezeBackend(IGameTypeRegistry types, GameEntityAccess entities, FrameDriverHost driver, GameClockPause clock, IPluginLog log)
    {
        _types = types;
        _entities = entities;
        _driver = driver;
        _clock = clock;
        _log = log;
        _lateTick = LateTick;
        _identityNow = IdentityNow;
        _flushOnUnfreeze = () => FlushDeferred("unfreeze");
    }

    public event Action? HoldDisabled;

    public bool HoldsPositions => _holding;

    /// <summary>Makes the time-scale and removal hooks installable (hot-update ready); they install on the first freeze.</summary>
    public void ArmHooks(HarmonyGameMethodHooker hooker) => _hooks.Arm(() =>
    {
        _clock.InstallHook(hooker);
        InstallRemovalGate(hooker);   // .Removal.cs
        InstallAnimGate(hooker);      // .Anim.cs: chained after the removal gate on RemoveEntity
    });

    public void EnsureHooks() => _hooks.Request();

    /// <summary>Pauses the clock, reads this press's entities and (optionally) starts the position hold. Each step runs
    /// through <see cref="FreezeStepRunner"/>: a step that throws still lets the later ones run, so <see cref="_frozen"/>,
    /// the late driver and a later <see cref="UnfreezeAll"/> stay consistent.</summary>
    public void FreezeAll(bool holdPositions)
    {
        if (_frozen) return;
        _frozen = true;
        _mainThread = Environment.CurrentManagedThreadId;   // FreezeAll runs on Unity's main thread
        _ledger.Clear();
        _removals.Arm();           // a monster killed from now on stays until the freeze ends (.Removal.cs)
        if (FreezeStepRunner.RunAll(_clock.Pause, ReadTargets, TrackAnim, () => { if (holdPositions) StartHold(); }) is { } ex)
            WarnOnce("freezeall", "freeze applied best-effort after an error: " + ex.Message);
        SyncLate();
        OnFrozen();
    }

    /// <summary>This press's entities: every entity uuid, minus the local player and their own mount (the hold never pins
    /// them — the local player is paused with everyone, and a hold would only fight a server correction of their own
    /// position). The selection itself is <see cref="FreezeTargets.Select"/> (unit-tested).</summary>
    private void ReadTargets() => FreezeTargets.Select(_entities, _ledger, _ids);

    /// <summary>Unfreezes in <see cref="FreezeTeardown"/>'s pinned order (deferred deaths replayed first, the clock resumed
    /// whatever fails before it), so the game is never left paused.</summary>
    public void UnfreezeAll()
    {
        if (!_frozen) return;
        _frozen = false;
        OnUnfreezing();
        if (FreezeTeardown.RunAfterFlush(_flushOnUnfreeze, _removals.Disarm, this) is { } ex)
            WarnOnce("unfreezeall", "unfreeze completed best-effort after an error: " + ex.Message);
        SyncLate();
        OnUnfrozen();
    }

    private void LateTick()
    {
        if (_holding)
        {
            try { HoldTick(); }
            catch (Exception ex) { WarnOnce("holdtick2", "the position hold failed this frame: " + ex.Message); }
        }
        SyncLate();
    }

    private void SyncLate()
    {
        var want = _frozen && _holding;
        if (want == _lateOn) return;
        _lateOn = want;
        _driver.SetLate(want ? _lateTick : null);
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _log.Warning(Tag + message);
    }

    partial void OnFrozen();
    partial void OnUnfreezing();
    partial void OnUnfrozen();
}
