using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for <see cref="PandaInventoryPullReader"/>. All
/// pull-read / resolution log emission lives here, gated on
/// <see cref="StellarDiagnostics.IsEnabled"/> for repeating events; a small
/// set of one-shot lines fire unconditionally (resolution outcome,
/// first sample) so non-diagnostic runs still get the framework-load
/// evidence the scenario gates look for.
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    // Recon helper. Fires once at first resolution attempt (regardless of
    // STELLAR_DIAGNOSTICS) to inform the next dev iteration which
    // CharSerialize accessor the runtime actually exposes — see
    // recon/phase-7-types.md §1 ("Probe needed for Task 12 follow-up").
    private bool _reconTypeDumpEmitted;

    // Read-path abort reasons. Surfaced under STELLAR_DIAGNOSTICS, rate-limited
    // to once-per-distinct-reason so a 1Hz poll doesn't spam the log. This tells
    // the next dev iteration exactly where the CharSerialize walk breaks even
    // though the accessor "resolved".
    internal enum ReadAbort
    {
        CharSerializeNull,
        ItemPackageNull,
        ModPackageAbsent,
        EmptyModPackage,
    }

    private readonly HashSet<ReadAbort> _loggedAborts = new();
    private bool _readOkLogged;

    private void OnResolutionSucceededLogged(Type charSerializeType)
    {
        // Always log success — this is one of the scenario-gate lines.
        _log.Info($"[Stellar][Inventory] container path resolved: {_charSerializeSource}");
        _log.Info("[Stellar][Inventory] file probe ready, polling 1Hz");

        // One-shot recon dump on SUCCESS too — lets the main session confirm the
        // chosen accessor + the live first-read shape without another round-trip.
        EmitOneShotReconTypeDump();
        EmitFirstReadProbe();
    }

    // Fires once after resolution succeeds: calls the chosen accessor and reports
    // whether it returned a non-empty CharSerialize. Disambiguates "resolved a
    // handle" from "the handle yields live data". Always on (one assembly read).
    private void EmitFirstReadProbe()
    {
        object? charSerialize;
        try { charSerialize = _readCharSerialize?.Invoke(); }
        catch (Exception ex)
        {
            _log.Info($"[Stellar][Inventory][Recon] first read threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (charSerialize is null)
        {
            _log.Info("[Stellar][Inventory][Recon] first read: accessor returned null CharSerialize (empty/default ECS component?)");
            return;
        }

        // Probe one level deep: does ItemPackage resolve and does the mod package exist?
        var diag = DescribeCharSerializeShape(charSerialize);
        _log.Info($"[Stellar][Inventory][Recon] first read: non-null CharSerialize via {_charSerializeSource}; {diag}");
    }

    internal string DescribeCharSerializeShape(object charSerialize)
    {
        if (_itemPackageProperty is null) return "ItemPackage property unresolved";
        object? itemPackage;
        try { itemPackage = _itemPackageProperty.GetValue(charSerialize); }
        catch (Exception ex) { return $"ItemPackage getter threw {ex.GetType().Name}"; }
        if (itemPackage is null) return "ItemPackage=null";

        if (_packagesProperty is null) return "ItemPackage non-null, Packages property unresolved";
        object? packagesMap;
        try { packagesMap = _packagesProperty.GetValue(itemPackage); }
        catch (Exception ex) { return $"Packages getter threw {ex.GetType().Name}"; }
        if (packagesMap is null) return "Packages=null";

        var modPackage = LookupMapValue(packagesMap, ModPackageKey);
        if (modPackage is null) return $"Packages present but no key {ModPackageKey} (mod package)";

        var itemCount = 0;
        if (_packageItemsProperty is null)
        {
            _packageItemsProperty = FindMapLikeProperty(modPackage.GetType(), "Items");
        }
        if (_packageItemsProperty is not null)
        {
            try
            {
                var itemsMap = _packageItemsProperty.GetValue(modPackage);
                if (itemsMap is not null)
                {
                    foreach (var _ in EnumerateMapValues(itemsMap)) itemCount++;
                }
            }
            catch { /* report best-effort */ }
        }
        return $"mod package key {ModPackageKey} present, {itemCount} raw items";
    }

    // Rate-limited read-abort log. Fires once per distinct reason under
    // STELLAR_DIAGNOSTICS so we can SEE where the 1Hz read bails without spam.
    private void OnReadAbort(ReadAbort reason)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (!_loggedAborts.Add(reason)) return;
        _log.Info($"[Stellar][Inventory] read abort: {ReadAbortMessage(reason)}");
    }

    private static string ReadAbortMessage(ReadAbort reason) => reason switch
    {
        ReadAbort.CharSerializeNull => "CharSerialize instance null",
        ReadAbort.ItemPackageNull => "ItemPackage null",
        ReadAbort.ModPackageAbsent => $"Mod package (key {ModPackageKey}) absent",
        ReadAbort.EmptyModPackage => "0 items in mod package",
        _ => reason.ToString(),
    };

    // Fires once per distinct (walked, filtered) shape under diagnostics so we
    // can confirm the read chain works end-to-end.
    private void OnReadOk(int walkedItems, int modulesAfterFilter)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_readOkLogged) return;
        _readOkLogged = true;
        _log.Info($"[Stellar][Inventory] read ok: walked {walkedItems} items, {modulesAfterFilter} modules after filter");
    }

    private void OnResolutionFailureLogged(string reason)
    {
        // Throttled log so a 1Hz poll doesn't spam the BepInEx log during
        // boot or character select. We emit on the first attempt and then
        // once per ResolutionFailureLogEvery attempts after that.
        var attempts = _failedResolutionAttempts;
        var shouldLog = !_resolutionFailureLogged
            || (attempts % ResolutionFailureLogEvery == 0);
        if (!shouldLog) return;

        _resolutionFailureLogged = true;
        _log.Info($"[Stellar][Inventory] container path resolution failed: {reason} (attempt {attempts})");

        EmitOneShotReconTypeDump();
    }

    // Dumps the loaded-type candidates the recon notes called out
    // (CharDataComponent / ICharDataObtain / ICharDataWatcherService /
    // *.CharSerialize). Fires once per process. Always on — the cost is
    // a single assembly walk and only happens when resolution failed
    // (otherwise we don't need the dump).
    private void EmitOneShotReconTypeDump()
    {
        if (_reconTypeDumpEmitted) return;
        _reconTypeDumpEmitted = true;

        var hits = CollectReconTypeHits();

        if (hits.Count == 0)
        {
            _log.Info("[Stellar][Inventory][Recon] no CharData/CharSerialize types found in loaded assemblies");
            return;
        }
        foreach (var hit in hits)
        {
            _log.Info($"[Stellar][Inventory][Recon] {hit}");
        }
    }

    // Scans loaded game assemblies for CharDataComponent, ICharDataObtain,
    // ICharDataWatcherService, and *.CharSerialize types. Returns one entry
    // per matching type in "asmName → fullName" format.
    private static List<string> CollectReconTypeHits()
    {
        var hits = new List<string>(capacity: 8);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName;
            try { asmName = asm.GetName().Name ?? string.Empty; }
            catch { continue; }
            // Filter to game assemblies — BCL / Unity / Il2CppInterop don't
            // host CharSerialize and some of them throw on GetTypes().
            if (string.IsNullOrEmpty(asmName)) continue;
            if (asmName.StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
            if (asmName.StartsWith("System", StringComparison.Ordinal)) continue;
            if (asmName.StartsWith("Microsoft", StringComparison.Ordinal)) continue;
            if (asmName.StartsWith("Il2Cpp", StringComparison.Ordinal)) continue;
            if (asmName.StartsWith("BepInEx", StringComparison.Ordinal)) continue;
            if (asmName.StartsWith("MonoMod", StringComparison.Ordinal)) continue;
            if (asmName.StartsWith("HarmonyX", StringComparison.Ordinal) || asmName == "0Harmony") continue;
            if (asmName.StartsWith("mscorlib", StringComparison.Ordinal) || asmName.StartsWith("netstandard", StringComparison.Ordinal)) continue;

            CollectReconTypeHitsFromAssembly(asm, asmName, hits);
        }
        return hits;
    }

    // Scans one assembly for recon-target types and appends matches to hits.
    private static void CollectReconTypeHitsFromAssembly(
        System.Reflection.Assembly asm,
        string asmName,
        List<string> hits)
    {
        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
        catch { return; }
        foreach (var t in types)
        {
            if (t is null) continue;
            string name;
            string? fullName;
            try { name = t.Name; fullName = t.FullName; } catch { continue; }
            if (name == "CharDataComponent"
                || name == "ICharDataObtain"
                || name == "ICharDataWatcherService"
                || (fullName is not null && fullName.EndsWith(".CharSerialize", StringComparison.Ordinal)))
            {
                hits.Add($"{asmName} → {fullName}");
            }
        }
    }

    // ── Multi-candidate probe logging ──
    // The candidate probe runs on EVERY resolution attempt until a winner is
    // found, so the per-candidate detail is gated on diagnostics to avoid
    // spam during the pre-character polling window. A small set of one-shot
    // markers (count built, winner selected) is recorded once per process so
    // even a non-diagnostic run shows which accessor was chosen.
    private int _lastCandidateCountLogged = -1;
    private bool _candidateSelectedLogged;
    private readonly HashSet<string> _loggedCandidateOutcomes = new();

    private bool _firstCaptureLogged;

    // Fires once when the OnCallStub postfix first latches a live CharSerialize
    // (decoded from the WorldNtf method-21 SyncContainerData full sync). Always
    // on — it's the proof the capture hook is live and marks the moment inventory
    // reads can succeed. We classify the captured payload IMMEDIATELY
    // (package/item counts) so the data-richness evidence lands at capture time,
    // independent of the 1Hz read poll — important because the in-world window
    // can be brief under the AutoNav driver.
    //
    // Invoked by the WireCapture collaborator (via the injected pull-reader
    // back-reference) on each method-21 full sync, so it is internal here.
    internal void OnCharSerializeCaptured()
    {
        // Real data just landed — clear the resolution backoff so the next 1Hz
        // poll re-attempts immediately (and resolves via the captured candidate)
        // instead of waiting out the backoff interval.
        _failedResolutionAttempts = 0;

        if (_firstCaptureLogged) return;
        _firstCaptureLogged = true;

        string detail;
        try
        {
            var cs = _state.CapturedCharSerialize;
            if (cs is null)
            {
                detail = "(read returned null)";
            }
            else
            {
                EnsureShapeHandles(cs.GetType());
                detail = DescribeCharSerializeShape(cs);
            }
        }
        catch (Exception ex) { detail = $"(classify threw {ex.GetType().Name})"; }

        _log.Info($"[Stellar][Inventory] captured live CharSerialize from WorldNtf SyncContainerData (method 21); {detail}");
    }

    // Per-hop diagnostic for the ZPureReader candidate. One-shot per distinct
    // message so a steadily-null hop (pre-character) logs once, but a state
    // change (null → OK, or a different failure point) still surfaces. Always
    // on while resolution is unsuccessful — this is the line that tells the
    // next iteration WHICH hop of the Pure-ECS chain breaks.
    private readonly HashSet<string> _loggedPureReaderHops = new();

    private void OnPureReaderHop(string message)
    {
        if (_resolutionSucceeded) return;
        if (!_loggedPureReaderHops.Add(message)) return;
        _log.Info($"[Stellar][Inventory][Probe] PureReader hop: {message}");
    }

    // Resets the probe-log dedup sets so a lifecycle transition (login / scene
    // enter) re-surfaces per-candidate + per-hop outcomes for the in-world
    // attempts. Without this the boot-time (pre-character) "null" results mask
    // whether the live attempts resolve.
    private void ClearProbeLogDedup()
    {
        _loggedCandidateOutcomes.Clear();
        _loggedPureReaderHops.Clear();
        _lastCandidateCountLogged = -1;
    }

    private void OnCandidatesBuilt(int count)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        // Re-log whenever the candidate count changes — the count grows once the
        // VContainer resolver attaches mid-run and adds the container-resolved
        // IPureComponentAccessor / ICharDataObtain / ICharDataWatcherService
        // candidates, which is exactly the transition we need to SEE.
        if (count == _lastCandidateCountLogged) return;
        _lastCandidateCountLogged = count;
        _log.Info($"[Stellar][Inventory][Probe] built {count} candidate accessor(s)");
    }

    private void OnCandidateProbed(CandidateAccessor candidate, CandidateOutcome outcome)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        // Rate-limit by (index, summary) so a steady empty result isn't logged
        // every tick, but a state CHANGE (empty → OK) still surfaces.
        var key = $"{candidate.Index}:{outcome.Summary}";
        if (!_loggedCandidateOutcomes.Add(key)) return;
        _log.Info($"[Stellar][Inventory][Probe] candidate {candidate.Index} ({candidate.Description}): {outcome.Summary}");
    }

    private void OnCandidateSelected(CandidateAccessor candidate)
    {
        // Always log the winner once — this is the line that proves which
        // accessor carried the live data.
        if (_candidateSelectedLogged) return;
        _candidateSelectedLogged = true;
        _log.Info($"[Stellar][Inventory][Probe] SELECTED candidate {candidate.Index} ({candidate.Description})");
    }

    // Called from TryReadModules after building the snapshot. Emits the
    // one-shot "first sample" line and the per-diff line.
    private void OnSampleLogged(int moduleCount, int equippedCount)
    {
        if (!_firstSampleLogged)
        {
            _firstSampleLogged = true;
            _log.Info($"[Stellar][Inventory] first sample: {moduleCount} modules, {equippedCount} equipped slots");
        }
    }

    // Called from TryReadEquipped — diff the equipped set against the
    // previous tick. Only fires under diagnostics to keep normal log volume
    // low; the InventoryService hash diff is the user-visible signal.
    private void OnEquippedDiffMaybeLogged(IReadOnlyDictionary<int, long> nextEquipped)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_lastEquippedSnapshot is null) return; // first sample handled by OnSampleLogged

        // Compare slot-by-slot. Diffs are typically 1–5 slots (ModSlotMaxCount; 4 before patch 3.7).
        foreach (var kv in nextEquipped)
        {
            if (!_lastEquippedSnapshot.TryGetValue(kv.Key, out var prev) || prev != kv.Value)
            {
                var from = prev == 0 ? "(empty)" : prev.ToString();
                _log.Info($"[Stellar][Inventory] snapshot diff: equip slot {kv.Key}: {from} → {kv.Value}");
            }
        }
        foreach (var kv in _lastEquippedSnapshot)
        {
            if (!nextEquipped.ContainsKey(kv.Key))
            {
                _log.Info($"[Stellar][Inventory] snapshot diff: equip slot {kv.Key}: {kv.Value} → (empty)");
            }
        }
    }
}
