using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;

namespace Stellar.Infrastructure.Game;

// Diagnostics live in PandaEntityStateProbe.Diagnostics.cs (ungated first-N-per-kind lines,
// ungated first-N-per-site unfiltered raw transitions, and StellarDiagnostics-gated detail).

/// <summary>
/// Surfaces the client's own entity state-machine transitions as a
/// <see cref="CombatEvent.EntityStateChanged"/> combat event (2026-07-28
/// entity-state-death-signal spec) so plugins can know an entity died — or entered its
/// break phase — without inferring it from HP reaching zero or a damage packet's death
/// flag. Both are unreliable for scripted kills: the owner's raid stage brings two
/// bosses to 1% and removes them via a triggered event, so HP never crosses zero and
/// <c>SyncDamageInfo.IsDead</c> never fires.
///
/// <para>
/// <b>2026-07-28 field result — the leaf patches installed cleanly and never fired.</b>
/// The owner cleared three dungeons with mob kills; <c>EntityCtrlDead.OnEnter</c> and
/// <c>ZStateBreaking.OnEnter</c> produced zero observations and zero soft-fail warnings —
/// both types resolved and patched, they simply are not what runs when a monster dies on
/// this build. Fresh <c>dnfile</c> recon against <c>Panda.ZGame.ZStateMachine</c>
/// (<c>recon/entity-state-death-signal-notes.md</c>) shows the machine itself is the
/// funnel: <c>onStateChanged(fromState, toState)</c> and <c>EnterState(targetState)</c>
/// fire on every transition and hand the state value directly as an argument, with no
/// dependency on which leaf state class ends up entered. These are now the PRIMARY
/// patch sites. The leaf sites (<c>EntityCtrlDead.OnEnter</c>, <c>ZStateBreaking.OnEnter</c>,
/// and the newly-added <c>ZStateDead.OnEnter</c> — the spec's named alternative, absent
/// from the FIRST round entirely) stay installed as a diagnostic round: we don't yet know
/// if ANY of them ever fire for anything, and finding out costs nothing once the machine
/// hooks already carry the real signal.
/// </para>
///
/// <para>
/// <b>Hot-path discipline is the governing constraint for the machine hooks.</b>
/// <c>onStateChanged</c>/<c>EnterState</c> fire for every actor's every transition — moves,
/// jumps, skills — not just death/break, so in an 18-player raid this is orders of
/// magnitude hotter than a death leaf ever was. The callbacks below unbox the state
/// argument to a raw <see langword="int"/> (via <see cref="Convert.ToInt32(object)"/> — a
/// BCL numeric conversion, NOT reflection) and run it through
/// <see cref="ActorStateMapper.MapWireValue"/> FIRST; only a Dead/Breaking match reaches
/// the reflected <c>Host</c> → <c>Uuid</c> getters. No reflected getter is invoked before
/// that filter passes.
/// </para>
///
/// <para>
/// Resolution is one-shot at <see cref="Install"/> time (called from
/// <c>BootstrapPlugin.OnHotUpdateReady</c>, after all 8 Panda hot-update assemblies —
/// including <c>Panda.Script</c>, which carries every type this probe needs — are
/// confirmed loaded; see <c>docs/il2cpp-probing-safety.md</c> and the
/// <c>HotkeyKeyBlockPatch</c> / <c>PandaWorldAttrProbe</c> precedents for the soft-fail
/// idiom). Every patch site resolves and installs independently: a missing type or
/// accessor degrades ONLY that one site to "feature off" (logged), never throws, and
/// never blocks any other site.
/// </para>
///
/// <para>
/// <b>De-duplication:</b> several sites can plausibly fire for the SAME real transition
/// (a controller-side state entry and a machine-side one, for the same death, close in
/// real time). <see cref="ShouldRaise"/> keys on (entity, state) with a short wall-clock
/// window so the plugin never sees the same logical transition raised twice, without
/// requiring us to already know which site is "the" live one — that's exactly what this
/// diagnostic round is for.
/// </para>
/// </summary>
internal sealed partial class PandaEntityStateProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string EntityCtrlDeadTypeName = "Panda.ZGame.EntityCtrlDead";
    private const string ZStateBreakingTypeName = "Panda.ZGame.ZStateBreaking";
    private const string ZStateDeadTypeName     = "Panda.ZGame.ZStateDead";
    private const string ZStateMachineTypeName  = "Panda.ZGame.ZStateMachine";
    private const string ZEntityTypeName        = "Panda.ZGame.ZEntity";

    private const string OnEnterMethodName        = "OnEnter";
    private const string OnStateChangedMethodName = "onStateChanged";
    private const string EnterStateMethodName     = "EnterState";

    // Site-attribution labels — every ungated observation line names one of these, so a
    // single run tells us which sites are actually live (2026-07-28 field-result review).
    private const string SiteOnStateChanged = "onStateChanged";
    private const string SiteEnterState     = "EnterState";
    private const string SiteZStateDead     = "ZStateDead";
    private const string SiteEntityCtrlDead = "EntityCtrlDead";
    private const string SiteZStateBreaking = "ZStateBreaking";

    // De-dup window: sites firing for the SAME real transition should land within the same
    // frame/call-stack, not seconds apart. 500ms comfortably covers same-instant multi-site
    // firing while still letting a genuine SECOND real transition of the same kind (e.g. a
    // boss re-entering Breaking a few seconds later) through. Wall-clock (TickCount64), not
    // ServerNowMs — this is about real elapsed time between postfix invocations, not the
    // server-time timestamp attached to the raised event.
    private const long DedupWindowMs = 500;

    private static readonly object?[] EmptyArgs = Array.Empty<object?>();

    private readonly IGameTypeRegistry _typeRegistry;
    private readonly ICombatEventSink  _sink;
    private readonly ICombatSnapshot   _combat;
    private readonly IPluginLog        _log;

    // Cached reflection handles — resolved once in Install(), never retried per-tick (unlike
    // EntityTransformsService's per-frame resolver): by the time Install() runs, hot-update
    // load is already confirmed complete, so a miss here means the type/member genuinely
    // isn't there (a game-patch shape change), not a load-order race.
    private MethodInfo? _uuidGetter;
    private MethodInfo? _machineHostGetter;    // ZStateMachine.Host
    private MethodInfo? _zStateDeadHostGetter; // ZStateDead.Host (inherited from ZState)
    private MethodInfo? _deadHostGetter;       // EntityCtrlDead.Host (inherited from StateCtrlBase)
    private MethodInfo? _breakingHostGetter;   // ZStateBreaking.Host (inherited from ZState)

    // (entity uuid, state) -> last-raised wall-clock tick. Cleared per scene alongside the
    // diagnostics budget (ResetObservationBudget) — dedup only needs to span one transition
    // burst, never across a scene boundary, so this bounds its growth over a long session.
    private readonly Dictionary<(long Uuid, ActorState State), long> _recentlyRaised = new();

    public PandaEntityStateProbe(IGameTypeRegistry typeRegistry, ICombatEventSink sink, ICombatSnapshot combat, IPluginLog log)
    {
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _sink         = sink         ?? throw new ArgumentNullException(nameof(sink));
        _combat       = combat       ?? throw new ArgumentNullException(nameof(combat));
        _log          = log          ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Resolves every patch site and installs whichever ones resolve. Safe to call
    /// exactly once; call after hot-update assemblies are confirmed loaded.
    /// </summary>
    public void Install(HarmonyGameMethodHooker hooker)
    {
        // This runs synchronously inside AppDomainHotUpdateWatcher.Observe's AssemblyLoad
        // event handler (via BootstrapPlugin.OnHotUpdateReady), which has no surrounding
        // try/catch of its own. GetProperty(name, flags) can throw AmbiguousMatchException
        // (and reflection against a hot-update type can throw in other unanticipated ways
        // after a game patch reshapes a type); every OTHER failure path in this file is
        // already null-safe — this is the one spot that wasn't, so an install-time
        // reflection surprise degrades to "signal off" (logged) instead of risking whatever
        // an unhandled exception does inside an AssemblyLoad handler.
        try
        {
            InstallCore(hooker);
        }
        catch (Exception ex)
        {
            _log.Warning($"[EntityState] Install threw {ex.GetType().Name}: {ex.Message}; entity-state signal disabled");
        }
    }

    private void InstallCore(HarmonyGameMethodHooker hooker)
    {
        var entityType = _typeRegistry.FindType(ZEntityTypeName);
        _uuidGetter = entityType?.GetProperty("Uuid", AnyInstance)?.GetGetMethod(nonPublic: true);
        if (entityType is null || _uuidGetter is null)
        {
            // Without ZEntity.Uuid no signal can resolve an EntityId — every site off.
            _log.Warning($"[EntityState] {ZEntityTypeName}.Uuid not found; entity-state signal disabled");
            return;
        }

        InstallMachinePatches(hooker);
        InstallZStateDeadPatch(hooker);
        InstallEntityCtrlDeadPatch(hooker);   // 2026-07-28: diagnostic round — never fired in round 1
        InstallZStateBreakingPatch(hooker);   // 2026-07-28: diagnostic round — never fired in round 1
    }

    // Resolves <typeName>'s inherited Host property. Logs+returns null on any miss so the
    // caller can skip installing that one site without touching any other.
    private MethodInfo? ResolveHostGetter(string typeName, string siteLabel, out Type? resolvedType)
    {
        resolvedType = _typeRegistry.FindType(typeName);
        var getter = resolvedType?.GetProperty("Host", AnyInstance)?.GetGetMethod(nonPublic: true);
        if (resolvedType is null || getter is null)
        {
            _log.Warning($"[EntityState] {typeName}.Host not found; {siteLabel} signal disabled");
        }
        return getter;
    }

    private void InstallMachinePatches(HarmonyGameMethodHooker hooker)
    {
        _machineHostGetter = ResolveHostGetter(ZStateMachineTypeName, "machine-level (onStateChanged/EnterState)", out var t);
        if (t is null || _machineHostGetter is null) return;
        hooker.PostfixAllOverloads(t, OnStateChangedMethodName, OnMachineStateChanged);
        hooker.PostfixAllOverloads(t, EnterStateMethodName, OnMachineEnterState);
    }

    private void InstallZStateDeadPatch(HarmonyGameMethodHooker hooker)
    {
        _zStateDeadHostGetter = ResolveHostGetter(ZStateDeadTypeName, SiteZStateDead, out var t);
        if (t is null || _zStateDeadHostGetter is null) return;
        hooker.PostfixAllOverloads(t, OnEnterMethodName, OnZStateDeadEnter);
    }

    private void InstallEntityCtrlDeadPatch(HarmonyGameMethodHooker hooker)
    {
        _deadHostGetter = ResolveHostGetter(EntityCtrlDeadTypeName, SiteEntityCtrlDead, out var t);
        if (t is null || _deadHostGetter is null) return;
        hooker.PostfixAllOverloads(t, OnEnterMethodName, OnEntityCtrlDeadEnter);
    }

    private void InstallZStateBreakingPatch(HarmonyGameMethodHooker hooker)
    {
        _breakingHostGetter = ResolveHostGetter(ZStateBreakingTypeName, SiteZStateBreaking, out var t);
        if (t is null || _breakingHostGetter is null) return;
        hooker.PostfixAllOverloads(t, OnEnterMethodName, OnZStateBreakingEnter);
    }

    // ---- Machine-level callbacks (PRIMARY signal; hot path — see class remarks) ----

    // onStateChanged(fromState, toState): args[0]=fromState, args[1]=toState. toState is
    // what the entity is transitioning INTO — the value the Dead/Breaking filter needs.
    private void OnMachineStateChanged(object? instance, object?[] args)
    {
        if (args.Length < 2 || !TryUnboxInt(args[1], out var toRaw)) return;
        var fromRaw = args.Length > 0 && TryUnboxInt(args[0], out var f) ? (int?)f : null;
        DiagUnfilteredTransition(SiteOnStateChanged, fromRaw, toRaw);

        var mapped = ActorStateMapper.MapWireValue(toRaw);
        if (mapped == ActorState.Unknown) return;   // fast exit — no reflection above this line
        RaiseIfHostResolves(instance, _machineHostGetter, mapped, SiteOnStateChanged);
    }

    // EnterState(targetState): args[0]=targetState — no fromState available at this site.
    private void OnMachineEnterState(object? instance, object?[] args)
    {
        if (args.Length < 1 || !TryUnboxInt(args[0], out var targetRaw)) return;
        DiagUnfilteredTransition(SiteEnterState, null, targetRaw);

        var mapped = ActorStateMapper.MapWireValue(targetRaw);
        if (mapped == ActorState.Unknown) return;
        RaiseIfHostResolves(instance, _machineHostGetter, mapped, SiteEnterState);
    }

    // ---- Leaf callbacks (diagnostic round — see class remarks) ----
    // The leaf OnEnter(fromState) argument is the state being LEFT, not entered (confirmed
    // via signature-blob decode, recon/entity-state-death-signal-notes.md) — so unlike the
    // machine hooks above, these hardcode the state implied by WHICH concrete type fired;
    // reading args here would read the wrong value, not a defensive cross-check.

    private void OnZStateDeadEnter(object? instance, object?[] args)
        => RaiseIfHostResolves(instance, _zStateDeadHostGetter, ActorState.Dead, SiteZStateDead);

    private void OnEntityCtrlDeadEnter(object? instance, object?[] args)
        => RaiseIfHostResolves(instance, _deadHostGetter, ActorState.Dead, SiteEntityCtrlDead);

    private void OnZStateBreakingEnter(object? instance, object?[] args)
        => RaiseIfHostResolves(instance, _breakingHostGetter, ActorState.Breaking, SiteZStateBreaking);

    // ---- Shared resolve + raise + de-dup ----

    // Reads Host off the instance that JUST executed the patched method (synchronous, same
    // call frame — not a later poll of an arbitrary id), so this does not fall into the
    // TOCTOU live-object class docs/il2cpp-probing-safety.md warns about. Only reached after
    // the caller's raw-int filter has already matched Dead/Breaking (machine sites) or by
    // construction (leaf sites) — never on every transition.
    private void RaiseIfHostResolves(object? instance, MethodInfo? hostGetter, ActorState state, string site)
    {
        if (instance is null || hostGetter is null || _uuidGetter is null) return;
        try
        {
            var host = hostGetter.Invoke(instance, EmptyArgs);
            if (host is null) return;
            if (_uuidGetter.Invoke(host, EmptyArgs) is not long uuid || uuid == 0) return;

            var entityId = new EntityId(uuid);
            if (!ShouldRaise(uuid, state))
            {
                DiagDuplicateSuppressed(site, state, entityId);
                return;
            }
            _sink.EnqueueEvent(new CombatEvent.EntityStateChanged(_combat.ServerNowMs, entityId, state));
            DiagFirstObserved(site, state, entityId);
        }
        catch (Exception ex)
        {
            DiagRaiseFailed(site, state, ex);
        }
    }

    // See DedupWindowMs for the window rationale. Only called after a Dead/Breaking match
    // and successful host/uuid resolution — never on the machine hooks' hot per-transition
    // path, so a Dictionary touch here costs nothing measurable.
    private bool ShouldRaise(long uuid, ActorState state)
    {
        var now = Environment.TickCount64;
        var key = (uuid, state);
        if (_recentlyRaised.TryGetValue(key, out var lastMs) && now - lastMs < DedupWindowMs)
        {
            return false;
        }
        _recentlyRaised[key] = now;
        return true;
    }

    // Convert.ToInt32 is a BCL numeric conversion (dispatches via the boxed value's built-in
    // IConvertible), NOT reflection — safe to call unconditionally on the machine hooks' hot
    // path. Returns false (never throws) for null or an unexpected boxed shape.
    private static bool TryUnboxInt(object? boxed, out int value)
    {
        value = 0;
        if (boxed is null) return false;
        try
        {
            value = Convert.ToInt32(boxed);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
