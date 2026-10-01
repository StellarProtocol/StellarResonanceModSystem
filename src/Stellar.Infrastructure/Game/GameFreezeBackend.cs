using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
using Stellar.Infrastructure.Unity;
namespace Stellar.Infrastructure.Game;

// Effects: GameFreezeBackend.Effects.cs. Animation (two stages): .Animation.cs. Position hold: .Hold.cs.
// Entities appearing while frozen: .Appear.cs. Logging: .Diagnostics.cs.

/// <summary>The game side of <c>ISceneFreeze</c> (spec § 4, recon runs 2 and 3) for every entity in
/// <c>ZEntityMgr.EntityDict</c>. Visual and local only. Each part fails open on its own with one warning; a failure in
/// one never stops the others. One LateUpdate handler runs stage 2, the appear re-checks and the hold; the frame driver
/// is off whenever none of them is live. Main thread.</summary>
internal sealed partial class GameFreezeBackend : ISceneFreezeBackend
{
    private const string Tag = "[FreeCam] ";
    /// <summary>Late frames between stage 1 (attr) and stage 2 (drawn speed) — run 3 read the drawn speed 2 frames on.</summary>
    private const int Stage2Delay = 2;

    private readonly IGameTypeRegistry _types;
    private readonly GameEntityAccess _entities;
    private readonly FrameDriverHost _driver;
    private readonly IPluginLog _log;
    private readonly FreezeLedger _ledger = new();
    private readonly LazyHookInstall _hooks = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly List<long> _ids = new();
    private readonly Action _lateTick;
    private bool _frozen;
    private bool _lateOn;
    private int _stage2Due;

    public GameFreezeBackend(IGameTypeRegistry types, GameEntityAccess entities, FrameDriverHost driver, IPluginLog log)
    {
        _types = types;
        _entities = entities;
        _driver = driver;
        _log = log;
        _lateTick = LateTick;
    }

    public event Action? HoldDisabled;

    public bool HoldsPositions => _holding;

    /// <summary>Makes the effect-creation and entity-appear hooks installable (hot-update ready); they install on the
    /// first freeze.</summary>
    public void ArmHooks(HarmonyGameMethodHooker hooker) => _hooks.Arm(() =>
    {
        InstallEffectHook(hooker);
        InstallAppearHook(hooker);
    });

    public void EnsureHooks() => _hooks.Request();

    /// <summary>Freezes effects, stage-1 animation and (optionally) the position hold for every entity read this
    /// press. Each phase runs through <see cref="FreezeStepRunner"/>: a phase that throws partway still lets the
    /// later phases run (best-effort per entity inside each phase too — see <c>FreezeFactor</c>/<c>TryHold</c>),
    /// so <see cref="_frozen"/>, the late driver and the diagnostics line stay consistent, and a later
    /// <see cref="UnfreezeAll"/> fully restores whatever this call actually froze.</summary>
    public void FreezeAll(bool holdPositions)
    {
        if (_frozen) return;
        _frozen = true;
        _ledger.Clear();
        _entities.EntityUuids(_ids);   // "everything on screen" = every entity, read once per press
        if (FreezeStepRunner.RunAll(FreezeEffects, FreezeAnimation, () => { if (holdPositions) StartHold(); }) is { } ex)
            WarnOnce("freezeall", "freeze applied best-effort after an error: " + ex.Message);
        _stage2Due = Stage2Delay;
        SyncLate();
        OnFrozen(_ledger.Effects.Count, _ledger.Factors.Count, _held.Count);
    }

    /// <summary>Unfreezes everything this backend touched. Each step runs through <see cref="FreezeStepRunner"/> so
    /// a throw restoring one piece (e.g. one entity's animation) never skips the others — the hold release, the
    /// effect unfreeze and the ledger clear always run, so the backend never gets stuck frozen.</summary>
    public void UnfreezeAll()
    {
        if (!_frozen) return;
        _frozen = false;
        _stage2Due = 0;
        _appeared.Clear();
        var effects = _ledger.Effects.Count;
        var factors = _ledger.Factors.Count;
        var speeds = _ledger.Speeds.Count;
        if (FreezeStepRunner.RunAll(StopHold, UnfreezeAnimation, UnfreezeEffects, _ledger.Clear) is { } ex)
            WarnOnce("unfreezeall", "unfreeze completed best-effort after an error: " + ex.Message);
        SyncLate();
        OnUnfrozen(effects, factors, speeds);
    }

    // Each stage wrapped in its own try (zero-allocation — no delegate/array needed for just 3 fixed calls): a
    // throw in one must not skip the others this frame. Stage 2 would otherwise be lost for good (_stage2Due is
    // already decremented to 0) and the appear re-check / hold write would never run this frame either.
    private void LateTick()
    {
        if (_stage2Due > 0 && --_stage2Due == 0)
        {
            try { FreezeDrawnSpeeds(); }
            catch (Exception ex) { WarnOnce("stage2tick", "freeze stage 2 failed this frame: " + ex.Message); }
        }
        if (_appeared.Count > 0)
        {
            try { TickAppeared(); }
            catch (Exception ex) { WarnOnce("appeartick2", "the appear watch failed this frame: " + ex.Message); }
        }
        if (_holding)
        {
            try { HoldTick(); }
            catch (Exception ex) { WarnOnce("holdtick2", "the position hold failed this frame: " + ex.Message); }
        }
        SyncLate();
    }

    private void SyncLate()
    {
        var want = _frozen && (_stage2Due > 0 || _appeared.Count > 0 || _holding);
        if (want == _lateOn) return;
        _lateOn = want;
        _driver.SetLate(want ? _lateTick : null);
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _log.Warning(Tag + message);
    }

    partial void OnFrozen(int effects, int factors, int held);
    partial void OnStage2(int drawnFrozen);
    partial void OnAppearFrozen(long uuid, int kind);
    partial void OnUnfrozen(int effects, int factors, int speeds);
}
