using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Tick half of <see cref="NativeUiService"/>. Driven from the host's UN-gated global beat (it must run through
/// zone loads, when the game rebuilds the battle HUD + chat — Game-Phases-and-Tick-Gating.md). Three cadences:
/// <list type="bullet">
/// <item>10 Hz liveness: per resolved element, "still alive?" and (customized only) "is our wrapper still in
///   place?" — a few interop reads, no allocation. A broken wrapper is re-wrapped + re-applied at once; applying
///   needs no measurement, so it works while the rebuilt element is still inactive (no snap-back frame).</item>
/// <item>2 Hz absent resolve: a path walk per absent element (boss bar outside fights, battle HUD not built).</item>
/// <item>1 Hz measure: ONE element per pass, only while it still needs it (legacy migration or no last-seen size at
///   this resolution yet) — the content-union walk is the expensive bit, so it is never polled steadily.</item>
/// </list>
/// Scene-enter signals arm an immediate liveness + resolve pass (<see cref="RequestRecheck"/>).
/// </summary>
internal sealed partial class NativeUiService
{
    private const float LivenessIntervalSeconds = 0.1f;   // 10 Hz cap (Plugin-Performance-Rules §1)
    private const float AbsentResolveSeconds    = 0.5f;
    private const float MeasureIntervalSeconds  = 1.0f;

    private float _clock;
    private float _livenessAcc, _absentAcc, _measureAcc;
    private bool _recheckArmed = true;   // first tick resolves immediately
    private bool _anyResolved;
    private int _measureCursor;

    public void Tick(float deltaTime, Resolution currentRes)
    {
        _clock += deltaTime;
        _lastResolution = currentRes;
        if (currentRes.Width <= 0 || currentRes.Height <= 0) return;
        _livenessAcc += deltaTime;
        _absentAcc += deltaTime;
        _measureAcc += deltaTime;

        var armed = _recheckArmed;
        if (!armed && _livenessAcc < LivenessIntervalSeconds) return;   // the common case: two float compares
        PerfProbe.BeginSeg("fw:nativeui");
        _recheckArmed = false;
        _livenessAcc = 0f;
        var resolveAbsent = armed || _absentAcc >= AbsentResolveSeconds;
        if (resolveAbsent) _absentAcc = 0f;
        CheckAll(currentRes, resolveAbsent);
        if (_measureAcc >= MeasureIntervalSeconds)
        {
            _measureAcc = 0f;
            MeasureNext();
            FlushSeen();
        }
        PerfProbe.EndSeg("fw:nativeui");
    }

    private void CheckAll(Resolution res, bool resolveAbsent)
    {
        var any = false;
        foreach (var e in _order)
        {
            if (!e.Loaded) LoadState(e, res, applyVisibility: true);
            if (e.IsResolved)
            {
                if (!_adapter.IsAlive(e.Handle))
                {
                    _log.Info($"[NativeUi] '{e.Descriptor.Id}' went stale (scene change?) — re-resolving");
                    e.IsResolved = false;
                    e.HasMeasure = false;
                    resolveAbsent = true;   // the rebuilt element usually exists already — bind it this pass
                }
                else if (NeedsWrap(e) && !_adapter.IsWrapIntact(e.Handle))
                {
                    // Log once per loss: a re-wrap that can't succeed yet (outer element momentarily detached) retries
                    // every pass and must not spam the log.
                    if (!e.WrapLossLogged) _log.Info($"[NativeUi] '{e.Descriptor.Id}' wrapper lost — re-wrapping");
                    e.WrapLossLogged = true;
                    ApplyToGame(e);
                }
                else e.WrapLossLogged = false;
            }
            if (!e.IsResolved && resolveAbsent) TryResolve(e);
            any |= e.IsResolved;
        }
        _anyResolved = any;
    }

    private void TryResolve(EntryState e)
    {
        if (!_adapter.TryResolve(e.Descriptor, out var handle)) return;
        e.Handle = handle;
        e.IsResolved = true;
        e.HasMeasure = false;
        _log.Info($"[NativeUi] resolved '{e.Descriptor.Id}'");
        ApplyToGame(e);   // in-memory state (loaded once per slot × resolution) — no config read on a rebuild
    }

    // One element per pass that still needs measuring: a pending legacy migration, or no fresh last-seen geometry
    // at this resolution. Elements the game isn't showing are skipped (retried next pass).
    private void MeasureNext()
    {
        for (var n = 0; n < _order.Length; n++)
        {
            var e = _order[_measureCursor];
            _measureCursor = (_measureCursor + 1) % _order.Length;
            if (!e.IsResolved || (e.LegacyRect is null && e.SeenFresh)) continue;
            var m = MeasureNow(e);
            if (m.Showing && e.LegacyRect is { } legacy) MigrateLegacy(e, legacy, m);
            return;
        }
    }

    // Convert a pre-wrapper saved screen top-left into a wrapper offset: offset = savedTopLeft − defaultTopLeft
    // (measured at offset 0 — a legacy entry is never moved before migrating), in wrapper-parent local units.
    private void MigrateLegacy(EntryState e, WindowRect legacy, Abstractions.NativeUiMeasure m)
    {
        var px = m.PxPerUnit > 0f ? m.PxPerUnit : PxPerUnit(e);
        // Old builds could save a flung, far off-screen rect (seen: x = -19700 at 1280x720); pull it on-screen first so a
        // migrated offset can never strand the element off-screen.
        var target = ClampFullyOnScreen(new WindowRect(legacy.X, legacy.Y, m.Rect.Width, m.Rect.Height), _lastResolution);
        var (ox, oy) = MigrateLegacyOffset(target.X, target.Y, DefaultRectOf(e, m), px);
        e.OffsetX = ox;
        e.OffsetY = oy;
        e.LegacyRect = null;
        e.HasMeasure = false;
        _log.Info($"[NativeUi] migrated '{e.Descriptor.Id}' saved rect → wrapper offset ({ox:0.#},{oy:0.#})");
        ApplyToGame(e);
        Persist(e);   // from now on the offset keys are authoritative (the old .x/.y keys are left in place)
    }
}
