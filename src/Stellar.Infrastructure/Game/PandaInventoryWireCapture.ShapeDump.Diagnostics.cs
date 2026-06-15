using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// DIAGNOSTIC-ONLY rich shape dump for <see cref="PandaInventoryWireCapture"/>'s
/// container-sync captures. Pure investigation — it does NOT latch, mutate, or
/// influence the read path or resolver in any way. Gated on
/// <see cref="StellarDiagnostics.IsEnabled"/> and rate-limited to the first few
/// occurrences of each method so it can't spam the BepInEx log.
///
/// <para>Goal: a single in-world session must reveal exactly which message +
/// which field path holds the player's modules (with their parts). The current
/// read path walks <c>CharSerialize.ItemPackage.Packages[5].Items</c> and finds
/// nothing even though the player has hundreds of modules in-game; recon §1 says
/// the dedicated mod container is <c>CharSerialize.Mod.ModInfos[uuid]</c> with
/// <c>PartIds</c>/<c>InitLinkNums</c>, so this dump compares both paths.</para>
///
/// <para>All log lines are prefixed <c>[Inventory][Shape]</c> so they grep
/// cleanly after the user reaches the Modules screen.</para>
/// </summary>
internal sealed partial class PandaInventoryWireCapture
{
    private const string ShapeTag = "[Inventory][Shape]";
    // Raised from 3 so a full in-world session surfaces the decisive side-by-side
    // evidence even if the early method-21 syncs land before the player reaches a
    // populated character. 12 is plenty to capture the post-login full sync while
    // still bounding log volume.
    private const int ShapeDumpMaxPerMethod = 12;

    private int _shape21Dumps;
    private int _shape22Dumps;

    private void DiagContainerShape(object stubCall, Type stubType, uint methodId)
    {
        if (!StellarDiagnostics.IsEnabled) return;

        try
        {
            if (methodId == WorldNtfMethodIds.SyncContainerData)
            {
                if (_shape21Dumps >= ShapeDumpMaxPerMethod) return;
                _shape21Dumps++;
                DumpMethod21Shape(stubCall, stubType);
            }
            else
            {
                if (_shape22Dumps >= ShapeDumpMaxPerMethod) return;
                _shape22Dumps++;
                DumpMethod22Shape(stubCall, stubType);
            }
        }
        catch (Exception ex)
        {
            _log.Info($"{ShapeTag} dump threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Method 21: SyncContainerData → CharSerialize (VData) ──
    private void DumpMethod21Shape(object stubCall, Type stubType)
    {
        // Decisive side-by-side evidence: raw payload length, then the
        // byte-parse-wrapper source vs the game's GetCallMsg object source. The
        // fix's claim is that the byte-parse carries Mod/ItemPackage while the
        // GetCallMsg object is drained — this line proves or disproves it.
        DumpDecodeSourceComparison(stubCall, stubType);

        var cs = DecodeCharSerialize(stubCall, stubType);
        if (cs is null)
        {
            _log.Info($"{ShapeTag} m21#{_shape21Dumps}: DecodeCharSerialize returned null");
            return;
        }

        var csType = cs.GetType();
        _log.Info($"{ShapeTag} m21#{_shape21Dumps}: CharSerialize type={csType.FullName}");

        DumpTopLevelNonNull(cs, csType, "CharSerialize");
        DumpModContainer(cs, csType);
        DumpItemPackage(cs, csType);
    }

    // Logs, for this method-21 call: the raw payload byte length and the
    // Mod/ItemPackage population of BOTH decode sources side by side.
    private void DumpDecodeSourceComparison(object stubCall, Type stubType)
    {
        var bytes = ExtractStubPayloadBytes(stubCall, stubType);
        var len = bytes?.Length ?? -1;

        var fromBytes = SummarizeCharSerialize(DecodeFromWrapperBytes(stubCall, stubType));
        var fromMsg = SummarizeCharSerialize(DecodeFromCallMsg(stubCall, stubType));

        _log.Info(
            $"{ShapeTag} m21#{_shape21Dumps}: payloadBytes={len} | byteParse[{fromBytes}] | getCallMsg[{fromMsg}]");
    }

    // Compact "Mod=.. ItemPackage=.." summary of a CharSerialize-like object's
    // module-bearing containers. Reports null vs counts so a drained object
    // (Mod=null/ItemPackage=null) reads distinctly from a populated one.
    private string SummarizeCharSerialize(object? cs)
    {
        if (cs is null) return "null";
        var csType = cs.GetType();
        var mod = SummarizeMod(cs, csType);
        var pkg = SummarizeItemPackage(cs, csType);
        return $"Mod:{mod} ItemPackage:{pkg}";
    }

    private string SummarizeMod(object cs, Type csType)
    {
        var modProp = csType.GetProperty("Mod", AnyInstance);
        if (modProp is null) return "<noProp>";
        object? mod;
        try { mod = modProp.GetValue(cs); } catch { return "<threw>"; }
        if (mod is null) return "null";
        var infos = CountMapLike(mod, mod.GetType(), "ModInfos");
        var slots = CountMapLike(mod, mod.GetType(), "ModSlots");
        return $"ModInfos={infos},ModSlots={slots}";
    }

    private string SummarizeItemPackage(object cs, Type csType)
    {
        var ipProp = csType.GetProperty("ItemPackage", AnyInstance);
        if (ipProp is null) return "<noProp>";
        object? ip;
        try { ip = ipProp.GetValue(cs); } catch { return "<threw>"; }
        if (ip is null) return "null";
        var pkgs = CountMapLike(ip, ip.GetType(), "Packages");
        return $"Packages={pkgs}";
    }

    // Enumerate every readable instance property of the CharSerialize and log
    // which ones are non-null (and a tiny value hint) — so module data hiding in
    // an unexpected field still shows up. Skips noisy proto plumbing props.
    private void DumpTopLevelNonNull(object cs, Type csType, string label)
    {
        PropertyInfo[] props;
        try { props = csType.GetProperties(AnyInstance); }
        catch (Exception ex) { _log.Info($"{ShapeTag} {label}: GetProperties threw {ex.GetType().Name}"); return; }

        var sb = new StringBuilder();
        var count = 0;
        foreach (var p in props)
        {
            if (!IsInterestingProtoProp(p)) continue;
            object? v;
            try { v = p.GetValue(cs); }
            catch { continue; }
            if (v is null) continue;
            if (count++ > 0) sb.Append(", ");
            sb.Append(p.Name).Append('=').Append(DescribeValueHint(v));
        }
        _log.Info($"{ShapeTag} {label} non-null props: {(count == 0 ? "(none)" : sb.ToString())}");
    }

    // CharSerialize.Mod → ModSlots + ModInfos. This is the recon-favoured
    // location for the modules. Dumps ONE ModInfo entry's full property set.
    private void DumpModContainer(object cs, Type csType)
    {
        var modProp = csType.GetProperty("Mod", AnyInstance);
        if (modProp is null) { _log.Info($"{ShapeTag} Mod: property not found on CharSerialize"); return; }

        object? mod;
        try { mod = modProp.GetValue(cs); }
        catch (Exception ex) { _log.Info($"{ShapeTag} Mod: getter threw {ex.GetType().Name}"); return; }
        if (mod is null) { _log.Info($"{ShapeTag} Mod=null"); return; }

        var modType = mod.GetType();
        var slotsCount = CountMapLike(mod, modType, "ModSlots");
        var infosProp = PandaInventoryPullReader.FindMapLikeProperty(modType, "ModInfos");
        object? infosMap = null;
        if (infosProp is not null)
        {
            try { infosMap = infosProp.GetValue(mod); } catch { infosMap = null; }
        }
        var infosCount = infosMap is null ? 0 : CountMapValues(infosMap);
        _log.Info($"{ShapeTag} Mod non-null: type={modType.FullName}, ModSlots count={slotsCount}, ModInfos count={infosCount}");

        if (infosMap is null || infosCount == 0) return;
        DumpOneModInfo(infosMap);
    }

    // Dump a single (uuid → ModInfo) entry: the key plus every readable property
    // of the ModInfo, with PartIds / InitLinkNums expanded to their int lists.
    private void DumpOneModInfo(object infosMap)
    {
        foreach (var (key, value) in PandaInventoryPullReader.EnumerateMapEntries(infosMap))
        {
            if (value is null) continue;
            var miType = value.GetType();
            _log.Info($"{ShapeTag} ModInfo sample: key(uuid)={PandaInventoryPullReader.AsInt64(key)}, type={miType.FullName}");

            PropertyInfo[] props;
            try { props = miType.GetProperties(AnyInstance); }
            catch { return; }

            foreach (var p in props)
            {
                if (!IsInterestingProtoProp(p)) continue;
                object? v;
                try { v = p.GetValue(value); }
                catch { continue; }
                _log.Info($"{ShapeTag}   ModInfo.{p.Name} = {DescribeModInfoMember(p.Name, v)}");
            }
            return; // ONE sample only.
        }
    }

    // CharSerialize.ItemPackage → Packages keys + per-package item counts. This
    // is the CURRENT (failing) read path — confirm whether it really is empty.
    private void DumpItemPackage(object cs, Type csType)
    {
        var ipProp = csType.GetProperty("ItemPackage", AnyInstance);
        if (ipProp is null) { _log.Info($"{ShapeTag} ItemPackage: property not found"); return; }

        object? ip;
        try { ip = ipProp.GetValue(cs); }
        catch (Exception ex) { _log.Info($"{ShapeTag} ItemPackage: getter threw {ex.GetType().Name}"); return; }
        if (ip is null) { _log.Info($"{ShapeTag} ItemPackage=null"); return; }

        var pkgsProp = PandaInventoryPullReader.FindMapLikeProperty(ip.GetType(), "Packages");
        object? pkgs = null;
        if (pkgsProp is not null)
        {
            try { pkgs = pkgsProp.GetValue(ip); } catch { pkgs = null; }
        }
        if (pkgs is null) { _log.Info($"{ShapeTag} ItemPackage non-null but Packages=null"); return; }

        var sb = new StringBuilder();
        var pkgCount = 0;
        foreach (var (key, value) in PandaInventoryPullReader.EnumerateMapEntries(pkgs))
        {
            var k = PandaInventoryPullReader.AsInt32(key);
            var items = 0;
            if (value is not null)
            {
                var itemsProp = PandaInventoryPullReader.FindMapLikeProperty(value.GetType(), "Items");
                if (itemsProp is not null)
                {
                    object? itemsMap;
                    try { itemsMap = itemsProp.GetValue(value); } catch { itemsMap = null; }
                    if (itemsMap is not null) items = CountMapValues(itemsMap);
                }
            }
            if (pkgCount++ > 0) sb.Append(", ");
            sb.Append($"key {k}: {items} items");
        }
        _log.Info($"{ShapeTag} ItemPackage.Packages: {(pkgCount == 0 ? "(empty)" : sb.ToString())} (mod package key={PandaInventoryPullReader.ModPackageKey})");
    }

    // ── Method 22: SyncContainerDirtyData → incremental delta body ──
    private void DumpMethod22Shape(object stubCall, Type stubType)
    {
        var getCallMsg = stubType.GetMethod("GetCallMsg", BindingFlags.Instance | BindingFlags.Public);
        object? msg = null;
        if (getCallMsg is not null)
        {
            try { msg = getCallMsg.Invoke(stubCall, null); } catch { msg = null; }
        }
        if (msg is null) { _log.Info($"{ShapeTag} m22#{_shape22Dumps}: GetCallMsg returned null"); return; }

        var concreteName = ResolveIl2CppConcreteType(msg);
        var concreteType = ResolveConcreteManagedType(concreteName, msg);
        if (concreteType is null)
        {
            _log.Info($"{ShapeTag} m22#{_shape22Dumps}: body il2cppType={concreteName ?? "?"}, no concrete managed type resolved");
            return;
        }

        var body = CastToConcrete(msg, concreteType) ?? msg;
        _log.Info($"{ShapeTag} m22#{_shape22Dumps}: dirty body type={concreteType.FullName}");
        DumpTopLevelNonNull(body, concreteType, "DirtyBody");
    }

    // ── shared value-description helpers (diagnostic only) ──

    // PartIds / InitLinkNums get expanded to their int lists; everything else
    // gets a compact hint. The two int lists are the load-bearing evidence
    // (AttrId list + value list per module).
    private string DescribeModInfoMember(string name, object? v)
    {
        if (v is null) return "null";
        if (name is "PartIds" or "InitLinkNums" or "ModParts")
        {
            var ints = PandaInventoryPullReader.CollectInts(v);
            return $"[{string.Join(",", ints)}] (count={ints.Count})";
        }
        return DescribeValueHint(v);
    }

    private string DescribeValueHint(object v)
    {
        switch (v)
        {
            case string s:
                return $"\"{s}\"";
            case int or long or uint or ulong or short or ushort or byte or bool or float or double:
                return v.ToString() ?? "?";
        }
        var t = v.GetType();
        // Map/Repeated-like: report a count so empty-vs-populated is visible.
        if (LooksEnumerable(t))
        {
            var n = CountMapValues(v);
            return $"{t.Name}(count={n})";
        }
        return t.Name;
    }

    // Skip proto plumbing properties that aren't payload (Parser, Descriptor,
    // CalculateSize, Clone, etc. are methods; we only walk properties, but proto
    // adds CASE/HasX scalar plumbing too — keep it lean).
    private static bool IsInterestingProtoProp(PropertyInfo p)
    {
        if (p.GetIndexParameters().Length != 0) return false;
        if (!p.CanRead) return false;
        var n = p.Name;
        if (n == "Parser" || n == "Descriptor") return false;
        if (n.EndsWith("Case", StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool LooksEnumerable(Type t)
    {
        if (t == typeof(string)) return false;
        return typeof(System.Collections.IEnumerable).IsAssignableFrom(t)
            || t.Name.Contains("MapField", StringComparison.Ordinal)
            || t.Name.Contains("RepeatedField", StringComparison.Ordinal);
    }

    private int CountMapLike(object owner, Type ownerType, string mapName)
    {
        var prop = PandaInventoryPullReader.FindMapLikeProperty(ownerType, mapName);
        if (prop is null) return -1;
        object? map;
        try { map = prop.GetValue(owner); } catch { return -1; }
        if (map is null) return 0;
        return CountMapValues(map);
    }

    private static int CountMapValues(object map)
    {
        // Prefer the cheap Count property when present (proto MapField/RepeatedField).
        var countProp = map.GetType().GetProperty("Count", AnyInstance);
        if (countProp is not null)
        {
            try { return PandaInventoryPullReader.AsInt32(countProp.GetValue(map)); } catch { /* fall through */ }
        }
        var n = 0;
        foreach (var _ in PandaInventoryPullReader.EnumerateMapValues(map)) n++;
        return n;
    }
}
