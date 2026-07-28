using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;

namespace Stellar.Infrastructure.Game;

// Diagnostics live in PandaEntityStateProbe.Diagnostics.cs (ungated first-N-per-kind lines,
// per-scene budgeted, plus StellarDiagnostics-gated detail).

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
/// <b>2026-07-28 — field-proven, cut down to what the evidence supports.</b> Two prior
/// rounds (see <c>recon/entity-state-death-signal-notes.md</c>) tried, in turn,
/// <c>EntityCtrlDead.OnEnter</c> / <c>ZStateBreaking.OnEnter</c> (installed cleanly, never
/// fired for a real kill), then patched <c>ZStateMachine.onStateChanged</c>/<c>EnterState</c>
/// on top as a wider funnel. The owner's third run resolved it decisively: <c>ZStateDead.OnEnter</c>
/// fired for all ten deaths in the run (<c>EntityCtrlDead.OnEnter</c> stayed silent across
/// those SAME ten — disproven, not merely untested). <c>ZStateMachine.EnterState</c> ALSO
/// fired correctly for every one of those ten and resolved <c>Dead</c> correctly — it was
/// caught and suppressed by the (now-removed) cross-site de-dup precisely because it agreed
/// with <c>ZStateDead</c>, which is the de-dup working as designed, not a defect in
/// <c>EnterState</c>. <c>ZStateMachine.onStateChanged</c> never fired at all — a genuine
/// negative result. This file now patches exactly two sites:
/// </para>
///
/// <list type="bullet">
/// <item><c>Panda.ZGame.ZStateDead.OnEnter</c> — PROVEN live; the sole source of
/// <see cref="ActorState.Dead"/>. Kept over the machine hooks not because they don't work
/// (<c>EnterState</c> demonstrably does) but because a leaf <c>OnEnter</c> fires ONLY on
/// the transition we care about, while the machine hooks fire on every actor's every
/// transition (movement, jumps, skills) — orders of magnitude hotter in an 18-player raid,
/// for the same signal.</item>
/// <item><c>Panda.ZGame.ZStateBreaking.OnEnter</c> — UNTESTED, not disproven (the owner's
/// run never hit a break phase) — kept because it is the direct sibling of the hook just
/// proven live (same <c>ZState</c> family, same <c>OnEnter</c> shape, same <c>Host</c>
/// resolution), and it doubles as an exact timestamp source for an open frametime-spike
/// investigation once it fires.</item>
/// </list>
///
/// <para>
/// <b>Not deleted, just not installed:</b> if a future game patch removes or renames
/// <c>ZStateDead</c>, <c>ZStateMachine.EnterState(EActorState targetState)</c> is a
/// field-PROVEN fallback — args[0] is the state actually being entered (unlike the leaf
/// <c>OnEnter</c>'s argument, which is the state being LEFT), and it resolved <c>Dead</c>
/// correctly for all ten observed deaths. Re-adding it means re-adding the raw-int filter
/// (<c>Convert.ToInt32</c> + <c>ActorState</c> match, no reflection until a match) this
/// file no longer carries, since with only two leaf sites left there is nothing left that
/// fires often enough to need it.
/// </para>
///
/// <para>
/// Resolution is one-shot at <see cref="Install"/> time (called from
/// <c>BootstrapPlugin.OnHotUpdateReady</c>, after all 8 Panda hot-update assemblies —
/// including <c>Panda.Script</c>, which carries every type this probe needs — are
/// confirmed loaded; see <c>docs/il2cpp-probing-safety.md</c> and the
/// <c>HotkeyKeyBlockPatch</c> / <c>PandaWorldAttrProbe</c> precedents for the soft-fail
/// idiom). Each of the two sites resolves and installs independently: a missing type or
/// accessor degrades ONLY that one signal to "feature off" (logged), never throws, and
/// never blocks the other.
/// </para>
/// </summary>
internal sealed partial class PandaEntityStateProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string ZStateDeadTypeName     = "Panda.ZGame.ZStateDead";
    private const string ZStateBreakingTypeName = "Panda.ZGame.ZStateBreaking";
    private const string ZEntityTypeName        = "Panda.ZGame.ZEntity";
    private const string OnEnterMethodName      = "OnEnter";

    // Site-attribution labels — every ungated observation line names one of these. Kept even
    // though there are only two sites now: it's what let one owner run distinguish "the live
    // site" from the other three patched-but-silent/redundant sites instead of an ambiguous
    // single line.
    private const string SiteZStateDead     = "ZStateDead";
    private const string SiteZStateBreaking = "ZStateBreaking";

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
    private MethodInfo? _zStateDeadHostGetter; // ZStateDead.Host (inherited from ZState)
    private MethodInfo? _breakingHostGetter;   // ZStateBreaking.Host (inherited from ZState)

    public PandaEntityStateProbe(IGameTypeRegistry typeRegistry, ICombatEventSink sink, ICombatSnapshot combat, IPluginLog log)
    {
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _sink         = sink         ?? throw new ArgumentNullException(nameof(sink));
        _combat       = combat       ?? throw new ArgumentNullException(nameof(combat));
        _log          = log          ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Resolves both sites and installs whichever ones resolve. Safe to call exactly once;
    /// call after hot-update assemblies are confirmed loaded.
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
            // Without ZEntity.Uuid neither site can resolve an EntityId — both off.
            _log.Warning($"[EntityState] {ZEntityTypeName}.Uuid not found; entity-state signal disabled");
            return;
        }

        InstallZStateDeadPatch(hooker);
        InstallZStateBreakingPatch(hooker);
    }

    // Resolves <typeName>'s inherited Host property. Logs+returns null on any miss so the
    // caller can skip installing that one site without touching the other.
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

    private void InstallZStateDeadPatch(HarmonyGameMethodHooker hooker)
    {
        _zStateDeadHostGetter = ResolveHostGetter(ZStateDeadTypeName, SiteZStateDead, out var t);
        if (t is null || _zStateDeadHostGetter is null) return;
        hooker.PostfixAllOverloads(t, OnEnterMethodName, OnZStateDeadEnter);
    }

    private void InstallZStateBreakingPatch(HarmonyGameMethodHooker hooker)
    {
        _breakingHostGetter = ResolveHostGetter(ZStateBreakingTypeName, SiteZStateBreaking, out var t);
        if (t is null || _breakingHostGetter is null) return;
        hooker.PostfixAllOverloads(t, OnEnterMethodName, OnZStateBreakingEnter);
    }

    // HarmonyGameMethodHooker.Callbacks signature: (instance, args). Runs on whatever thread
    // invoked OnEnter — the game's own state-machine tick, i.e. the Unity main thread for
    // every entity's controller/state update (docs/coding-standards.md § Threading). Neither
    // callback reads args: the leaf OnEnter's single argument is the state being LEFT, not
    // entered (confirmed via signature-blob decode — recon/entity-state-death-signal-notes.md),
    // so the ActorState is hardcoded from WHICH concrete OnEnter fired, which is unambiguous
    // by construction.
    private void OnZStateDeadEnter(object? instance, object?[] args)
        => RaiseIfHostResolves(instance, _zStateDeadHostGetter, ActorState.Dead, SiteZStateDead);

    private void OnZStateBreakingEnter(object? instance, object?[] args)
        => RaiseIfHostResolves(instance, _breakingHostGetter, ActorState.Breaking, SiteZStateBreaking);

    // Reads Host off the instance that JUST executed OnEnter (synchronous, same call frame —
    // not a later poll of an arbitrary id), so this does not fall into the TOCTOU live-object
    // class docs/il2cpp-probing-safety.md warns about. ZStateDead and ZStateBreaking are
    // different states that cannot both fire for one transition, so — unlike the diagnostic
    // round that also patched ZStateMachine — there is no cross-site duplicate to de-dup here.
    private void RaiseIfHostResolves(object? instance, MethodInfo? hostGetter, ActorState state, string site)
    {
        if (instance is null || hostGetter is null || _uuidGetter is null) return;
        try
        {
            var host = hostGetter.Invoke(instance, EmptyArgs);
            if (host is null) return;
            if (_uuidGetter.Invoke(host, EmptyArgs) is not long uuid || uuid == 0) return;

            var entityId = new EntityId(uuid);
            _sink.EnqueueEvent(new CombatEvent.EntityStateChanged(_combat.ServerNowMs, entityId, state));
            DiagFirstObserved(site, state, entityId);
        }
        catch (Exception ex)
        {
            DiagRaiseFailed(site, state, ex);
        }
    }
}
