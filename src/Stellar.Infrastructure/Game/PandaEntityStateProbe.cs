using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;

namespace Stellar.Infrastructure.Game;

// Diagnostics live in PandaEntityStateProbe.Diagnostics.cs (ungated first-N-per-kind lines +
// per-event StellarDiagnostics-gated logging).

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
/// Patches the CONCRETE state's <c>OnEnter</c> — <c>Panda.ZGame.EntityCtrlDead</c> for
/// death, <c>Panda.ZGame.ZStateBreaking</c> for break phase — not the virtual base
/// (<c>StateCtrlBase</c> / <c>ZState</c>), because HarmonyX patching the base method
/// would miss overrides that don't chain to it. Confirmed via
/// <c>tools/dump-types.py</c> against <c>recon/cpp2il-out/Panda.Script.dll</c>: both
/// concrete types declare their own <c>OnEnter</c>, and both inherit <c>Host</c>
/// (<c>get_Host</c> / <c>host_</c>) typed <c>Panda.ZGame.ZEntity</c> from their
/// respective bases (<c>StateCtrlBase</c> for the former, <c>ZState</c> for the
/// latter) — same host-resolution shape on both, so one probe covers both sites.
/// </para>
///
/// <para>
/// Resolution is one-shot at <see cref="Install"/> time (called from
/// <c>BootstrapPlugin.OnHotUpdateReady</c>, after all 8 Panda hot-update assemblies —
/// including <c>Panda.Script</c>, which carries every type this probe needs — are
/// confirmed loaded; see <c>docs/il2cpp-probing-safety.md</c> and the
/// <c>HotkeyKeyBlockPatch</c> / <c>PandaWorldAttrProbe</c> precedents for the
/// soft-fail idiom). Each of the two patch sites resolves and installs independently:
/// a missing type or accessor degrades ONLY that one signal to "feature off" (logged),
/// never throws, and never blocks the other site.
/// </para>
/// </summary>
internal sealed partial class PandaEntityStateProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string EntityCtrlDeadTypeName = "Panda.ZGame.EntityCtrlDead";
    private const string ZStateBreakingTypeName = "Panda.ZGame.ZStateBreaking";
    private const string ZEntityTypeName        = "Panda.ZGame.ZEntity";
    private const string OnEnterMethodName      = "OnEnter";

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
    private MethodInfo? _deadHostGetter;
    private MethodInfo? _breakingHostGetter;

    public PandaEntityStateProbe(IGameTypeRegistry typeRegistry, ICombatEventSink sink, ICombatSnapshot combat, IPluginLog log)
    {
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _sink         = sink         ?? throw new ArgumentNullException(nameof(sink));
        _combat       = combat       ?? throw new ArgumentNullException(nameof(combat));
        _log          = log          ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Resolves both patch sites and installs whichever ones resolve. Safe to call
    /// exactly once; call after hot-update assemblies are confirmed loaded.
    /// </summary>
    public void Install(HarmonyGameMethodHooker hooker)
    {
        var entityType = _typeRegistry.FindType(ZEntityTypeName);
        _uuidGetter = entityType?.GetProperty("Uuid", AnyInstance)?.GetGetMethod(nonPublic: true);
        if (entityType is null || _uuidGetter is null)
        {
            // Without ZEntity.Uuid neither signal can resolve an EntityId — both sites off.
            _log.Warning($"[EntityState] {ZEntityTypeName}.Uuid not found; entity-state signal disabled");
            return;
        }

        InstallDeadPatch(hooker);
        InstallBreakingPatch(hooker);
    }

    private void InstallDeadPatch(HarmonyGameMethodHooker hooker)
    {
        var deadType = _typeRegistry.FindType(EntityCtrlDeadTypeName);
        _deadHostGetter = deadType?.GetProperty("Host", AnyInstance)?.GetGetMethod(nonPublic: true);
        if (deadType is null || _deadHostGetter is null)
        {
            _log.Warning($"[EntityState] {EntityCtrlDeadTypeName}.Host not found; Dead signal disabled");
            return;
        }
        hooker.PostfixAllOverloads(deadType, OnEnterMethodName, OnEntityCtrlDeadEnter);
    }

    private void InstallBreakingPatch(HarmonyGameMethodHooker hooker)
    {
        var breakingType = _typeRegistry.FindType(ZStateBreakingTypeName);
        _breakingHostGetter = breakingType?.GetProperty("Host", AnyInstance)?.GetGetMethod(nonPublic: true);
        if (breakingType is null || _breakingHostGetter is null)
        {
            _log.Warning($"[EntityState] {ZStateBreakingTypeName}.Host not found; Breaking signal disabled");
            return;
        }
        hooker.PostfixAllOverloads(breakingType, OnEnterMethodName, OnZStateBreakingEnter);
    }

    // HarmonyGameMethodHooker.Callbacks signature: (instance, args). Runs on whatever thread
    // invoked OnEnter — the game's own state-machine tick, i.e. the Unity main thread for
    // every entity's controller/state update (docs/coding-standards.md § Threading).
    private void OnEntityCtrlDeadEnter(object? instance, object?[] args)
        => TryRaise(instance, args, ActorState.Dead, _deadHostGetter);

    private void OnZStateBreakingEnter(object? instance, object?[] args)
        => TryRaise(instance, args, ActorState.Breaking, _breakingHostGetter);

    // Reads Host off the state instance that JUST executed OnEnter (synchronous, same call
    // frame — not a later poll of an arbitrary id), so this does not fall into the TOCTOU
    // live-object class docs/il2cpp-probing-safety.md warns about. Still defensive: any
    // reflection/marshal failure is swallowed (this is a HarmonyX postfix trust boundary —
    // HarmonyGameMethodHooker.Trampoline already catches everything, but failing fast here
    // keeps the intent local) and simply skips this one transition.
    private void TryRaise(object? instance, object?[] args, ActorState patchedState, MethodInfo? hostGetter)
    {
        if (instance is null || hostGetter is null || _uuidGetter is null) return;
        try
        {
            var host = hostGetter.Invoke(instance, EmptyArgs);
            if (host is null) return;
            if (_uuidGetter.Invoke(host, EmptyArgs) is not long uuid || uuid == 0) return;

            var state = ResolveState(args, patchedState);
            var entityId = new EntityId(uuid);
            _sink.EnqueueEvent(new CombatEvent.EntityStateChanged(_combat.ServerNowMs, entityId, state));
            DiagFirstObserved(state, entityId);
        }
        catch (Exception ex)
        {
            DiagRaiseFailed(patchedState, ex);
        }
    }

    // Prefers the game's own EActorState argument (confirms the wire taxonomy matches what
    // this patch site assumed); falls back to the value implied by WHICH concrete OnEnter
    // fired when the argument can't be read/mapped. That fact alone is already conclusive —
    // EntityCtrlDead.OnEnter only ever means Dead — so a marshal failure on the argument
    // never costs the signal, only the cross-check.
    private static ActorState ResolveState(object?[] args, ActorState patchedState)
    {
        if (args.Length > 0 && args[0] is not null)
        {
            try
            {
                var mapped = ActorStateMapper.MapWireValue(Convert.ToInt32(args[0]));
                if (mapped != ActorState.Unknown) return mapped;
            }
            catch
            {
                // Unexpected boxed shape for the EActorState arg — fall through to the
                // patch-site literal; ResolveState never throws into TryRaise's caller.
            }
        }
        return patchedState;
    }
}
