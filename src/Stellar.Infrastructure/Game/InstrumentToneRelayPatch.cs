using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Always-on listener-side band tone relay. A HarmonyX PREFIX on
/// <c>Panda.ZGame.InstrumentService.onInstrumentPlayEvent(ZEntity, RepeatedField&lt;Zproto.InstrumentSyncData&gt;)</c>
/// (dump.cs:228479 — both params are reference types, so no in-struct / Nullable trampoline crash) that
/// reverse-maps a small-int guitar/bass distortion TONE code back to the real <c>MusicalTimbre</c> id and
/// applies it so a REMOTE performer's tone renders on this (listener's) machine.
///
/// <para><b>Wire protocol (must match the Maestro-plugin sender exactly):</b> the sender encodes a tone as
/// <c>MusicalTimbre id − 1000000</c> (<c>1000000→0 … 1000006→6</c>). The game's server relays these small
/// ints in a Tone sync record but DROPS the raw <c>1000xxx</c> id (>1000), which is why vanilla tone never
/// reaches a listener through the note stream. This prefix undoes the encode on receive.</para>
///
/// <para><b>Why the swap runs here and targets the SERVER player:</b> the wire→<c>PendingInstrumentSync</c>
/// copy happens DOWNSTREAM of <c>onInstrumentPlayEvent</c>, so mutating the <c>RepeatedField</c> in this
/// prefix is captured. A remote performer's notes render on the listener through the entity's SERVER
/// <c>InstrumentPlayer</c>, so the tone must be applied with <c>isLocal:false</c> — applying it to the
/// LOCAL player is why tone never rendered in earlier attempts. See Band-Instrument-Playback.md
/// "⭐ Tone-relay REVISITED — 3-player split".</para>
///
/// <para><b>Vanilla-safe:</b> the game only ever puts the RAW <c>1000xxx</c> id in a Tone record (never
/// 0–6), so this only ever fires on our own remapped signal. A raw id that somehow arrives is passed
/// through untouched (forward-compat). Any exception is swallowed and the original runs — a throw here
/// would break band audio for every player.</para>
/// </summary>
internal static class InstrumentToneRelayPatch
{
    private const int ToneSyncType = 2;      // Zproto.EInstrumentSyncType.EInstrumentSyncTypeTone (dump.cs:697804)
    private const int RawToneBase = 1000000; // MusicalTimbre id base; wire code = id − RawToneBase (0..6)
    private const int MaxSmallTone = 6;      // guitar/bass distortion tone codes 0..6 (1000000..1000006)

    private static Action<string>? _log;
    private static bool _resolved;
    private static MethodInfo? _setTone;         // InstrumentService.EntityInstrumentSetTone(ZEntity, int, bool)
    private static MethodInfo? _syncTypeGetter;  // InstrumentSyncData.get_SyncType
    private static MethodInfo? _playParamGetter; // InstrumentSyncData.get_PlayParam
    private static MethodInfo? _playParamSetter; // InstrumentSyncData.set_PlayParam

    /// <summary>Mints a namespaced <see cref="Harmony"/> instance from the framework's own
    /// <see cref="IHarmonyHost"/> (auto-unpatches on framework teardown), resolves the target method
    /// reflectively, and installs the prefix. Non-throwing: any resolution/patch failure is logged and
    /// skipped so a missing game type never takes down framework boot. Taking the host (not a bare
    /// <see cref="Harmony"/>) keeps the HarmonyX dependency inside Infrastructure — the Host layer only
    /// touches the <see cref="IHarmonyHost"/> interface.</summary>
    public static void Install(IHarmonyHost host, Action<string> log)
    {
        _log = log;

        var harmony = host.Create("tonerelay");

        var svcType = StellarInterop.FindType("Panda.ZGame.InstrumentService");
        if (svcType is null)
        {
            log("[ToneRelay] Panda.ZGame.InstrumentService not found; band tone relay not installed");
            return;
        }

        // onInstrumentPlayEvent(ZEntity, RepeatedField<InstrumentSyncData>) — private, 2 params.
        var target = StellarInterop.FindMethod(svcType, "onInstrumentPlayEvent", 2);
        if (target is null)
        {
            log("[ToneRelay] InstrumentService.onInstrumentPlayEvent(ZEntity, RepeatedField) not found; band tone relay not installed");
            return;
        }

        var prefix = typeof(InstrumentToneRelayPatch).GetMethod(
            nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            log("[ToneRelay] Prefix method missing (build error); band tone relay not installed");
            return;
        }

        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            log("[ToneRelay] patched InstrumentService.onInstrumentPlayEvent (PREFIX) — remaps small-int Tone codes 0..6 → 1000000+ on receive (server player)");
        }
        catch (Exception ex)
        {
            log($"[ToneRelay] failed to patch onInstrumentPlayEvent: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // __instance = the InstrumentService; __args[0] = ZEntity entity; __args[1] = RepeatedField<InstrumentSyncData>.
    // Untyped injection (object / object?[]) so this holds no compile-time reference to the game/interop types —
    // every member is resolved reflectively via StellarInterop. Returns true unconditionally (always run the
    // original); swallows every exception because a throw here breaks band audio for all players in range.
    private static bool Prefix(object __instance, object?[] __args)
    {
        try
        {
            if (__instance is null || __args is null || __args.Length < 2) return true;
            var entity = __args[0];
            var events = __args[1];
            if (entity is null || events is null) return true;

            // RepeatedField<T> is NOT IEnumerable in IL2CPP — walk it with Count + get_Item(i).
            var n = StellarInterop.Count(events);
            if (n == 0) return true;

            if (!EnsureResolved(__instance, events)) return true;

            for (var i = 0; i < n; i++)
            {
                var rec = StellarInterop.Item(events, i);
                if (rec is null) continue;

                var syncTypeObj = _syncTypeGetter!.Invoke(rec, null);
                if (syncTypeObj is null || Convert.ToInt32(syncTypeObj) != ToneSyncType) continue;

                var playParamObj = _playParamGetter!.Invoke(rec, null);
                if (playParamObj is null) continue;
                var code = Convert.ToInt32(playParamObj);

                // Only our own encoded signal (0..6) is remapped. A raw 1000xxx id (>=RawToneBase, from a
                // future sender) passes through untouched — vanilla never emits 0–6 in a Tone record.
                if (code < 0 || code > MaxSmallTone) continue;

                var realTone = RawToneBase + code;

                // (a) Rewrite PlayParam in place so the downstream wire→PendingInstrumentSync copy carries the
                // real id. InstrumentSyncData is a reference type, so mutating the retrieved record mutates the
                // element inside the RepeatedField — no write-back needed.
                _playParamSetter!.Invoke(rec, new object[] { realTone });

                // (b) Apply the tone to the SERVER player (isLocal:false) BEFORE the queued Note records drain —
                // that is the player through which a remote performer's notes render on this machine.
                _setTone?.Invoke(__instance, new object[] { entity, realTone, false });
            }
        }
        catch { /* swallow — never break band audio */ }

        return true; // always run the original
    }

    // Resolves EntityInstrumentSetTone (off the live InstrumentService type) and the InstrumentSyncData
    // property accessors (off the live element type). Returns true once all four are resolved; does NOT
    // latch on failure so a call that happens before the types are fully loaded simply retries next batch.
    private static bool EnsureResolved(object svc, object events)
    {
        if (_resolved) return true;

        try
        {
            _setTone ??= StellarInterop.FindMethod(svc.GetType(), "EntityInstrumentSetTone", 3);

            if (_syncTypeGetter is null || _playParamGetter is null || _playParamSetter is null)
            {
                var sample = StellarInterop.Item(events, 0);
                if (sample is not null)
                {
                    var recType = sample.GetType();
                    _syncTypeGetter = StellarInterop.FindPropertyUp(recType, "SyncType")?.GetGetMethod(nonPublic: true);
                    var playParam = StellarInterop.FindPropertyUp(recType, "PlayParam");
                    _playParamGetter = playParam?.GetGetMethod(nonPublic: true);
                    _playParamSetter = playParam?.GetSetMethod(nonPublic: true);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[ToneRelay] member resolution threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        var ok = _setTone is not null && _syncTypeGetter is not null
                 && _playParamGetter is not null && _playParamSetter is not null;
        if (ok)
        {
            _resolved = true;
            _log?.Invoke("[ToneRelay] resolved EntityInstrumentSetTone + InstrumentSyncData.SyncType/PlayParam accessors");
        }
        return ok;
    }
}
