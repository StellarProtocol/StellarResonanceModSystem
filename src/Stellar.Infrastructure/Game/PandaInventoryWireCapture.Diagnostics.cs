using System;
using System.Reflection;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WorldNtf capture-side diagnostic sibling partial for
/// <see cref="PandaInventoryWireCapture"/>. Holds the byte-parse / decode log
/// emission for the stub-capture concern, gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>. The pull-read / resolution
/// diagnostics live on <see cref="PandaInventoryPullReader"/>.
/// </summary>
internal sealed partial class PandaInventoryWireCapture
{
    // ── Byte-parse diagnostics (from StubCapture.ByteParse.cs) ──

    private bool _wrapperResolveLogged;
    private bool _wrapperSignaturesLogged;

    // One-shot: log the byte-parse resolution outcome (null wrapper type, missing ParseFrom, etc.).
    private void DiagWrapperResolve(string outcome)
    {
        if (!StellarDiagnostics.IsEnabled || _wrapperResolveLogged) return;
        _wrapperResolveLogged = true;
        _log.Info($"[Inventory] wrapper byte-parse resolution: {outcome}");
    }

    // One-shot: dump every static ParseFrom/MergeFrom on the wrapper type and on
    // its Parser property's type, with full parameter type names. Il2CppInterop
    // generated protobuf often exposes ParseFrom(Il2CppStructArray<byte>) or a
    // CodedInputStream/ByteString overload rather than a managed byte[] — this
    // reveals the real signature so the resolver can target it.
    private void DiagWrapperParseSignatures(Type wrapperType)
    {
        if (!StellarDiagnostics.IsEnabled || _wrapperSignaturesLogged) return;
        _wrapperSignaturesLogged = true;
        try
        {
            LogParseishMethods(wrapperType, "wrapper");
            var parserProp = wrapperType.GetProperty("Parser", BindingFlags.Static | BindingFlags.Public);
            object? parser = null;
            try { parser = parserProp?.GetValue(null); } catch { /* ignore */ }
            if (parserProp is not null)
            {
                var ptype = parser?.GetType() ?? parserProp.PropertyType;
                LogParseishMethods(ptype, "parser");
            }
            else
            {
                _log.Info("[Inventory] wrapper has no static Parser property");
            }
        }
        catch (Exception ex) { _log.Info($"[Inventory] parse-sig dump threw {ex.GetType().Name}"); }
    }

    private void LogParseishMethods(Type t, string label)
    {
        MethodInfo[] methods;
        try { methods = t.GetMethods(AnyInstance | BindingFlags.Static); }
        catch { _log.Info($"[Inventory] {label} {t.FullName}: GetMethods threw"); return; }
        var sb = new StringBuilder();
        foreach (var m in methods)
        {
            if (m.Name is not ("ParseFrom" or "MergeFrom")) continue;
            sb.Append(m.IsStatic ? "static " : "inst ").Append(m.Name).Append('(');
            var ps = m.GetParameters();
            for (var i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.Name);
            }
            sb.Append("); ");
        }
        _log.Info($"[Inventory] {label} {t.FullName} parse methods: {(sb.Length == 0 ? "(none)" : sb.ToString())}");
    }

    // ── Self-gear decode diagnostics (from StubCapture.cs) ──

    private bool _gearDecodedLogged;

    // One-shot: log the first self-gear decode (method-21 bytes →
    // GearInstanceReader → sink) so an in-world run confirms the path.
    private void DiagGearDecoded(int count)
    {
        if (!StellarDiagnostics.IsEnabled || _gearDecodedLogged) return;
        _gearDecodedLogged = true;
        _log.Info($"[Inventory][Gear] decoded {count} gear instances");
    }

    // ── Container-merge event diagnostics (from StubCapture.cs HandleDirtyContainerFromBytes) ──
    //
    // Banks what a real edit actually emits. The per-field trigger allowlist that shipped before
    // 2026-08-23 silently dropped the gear UI's "Replace" button and imagine swaps, and NOTHING ever
    // logged the field list of a delta it rejected — so the gap was invisible for a whole session.
    // Both of these fire per event (not one-shot): the census IS the measurement, and a merge storm
    // only happens under a deliberate player action.

    private void DiagContainerMerge(byte[] buffer)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        try
        {
            var fields = Protobuf.ContainerDirtyDeltaReader.TopLevelFields(buffer);
            _log.Info($"[Inventory][Merge] container delta bytes={buffer.Length} fields=[{string.Join(",", fields)}]");
        }
        catch { /* diagnostics must never disturb the receive thread */ }
    }

    /// <summary>Names the stage at which the m22 envelope walk gave up. Previously every one of these
    /// bails returned silently, which is indistinguishable from "the player changed nothing".</summary>
    private void DiagMergeEnvelopeFail(string stage, int inputLength)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        try { _log.Info($"[Inventory][Merge] envelope extraction FAILED at {stage} (input {inputLength} bytes) — no merge signal raised"); }
        catch { /* diagnostics must never disturb the receive thread */ }
    }

    // ── Decode diagnostics (from StubCapture.Decode.cs) ──

    private bool _wrapperMembersLogged;

    // One-shot: log all properties on a wrapper type when it has no CharSerialize member.
    private void DiagWrapperMembers(Type concreteType)
    {
        if (!StellarDiagnostics.IsEnabled || _wrapperMembersLogged) return;
        _wrapperMembersLogged = true;
        try
        {
            var sb = new StringBuilder();
            foreach (var p in concreteType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try { sb.Append(p.PropertyType.Name).Append(' ').Append(p.Name).Append("; "); }
                catch { /* skip */ }
            }
            _log.Info($"[Inventory] wrapper {concreteType.FullName} has no CharSerialize member; props: {sb}");
        }
        catch { /* best-effort */ }
    }

    // One-shot per method: log the first observation of each container-sync method's
    // decoded body shape (concrete IL2CPP type from GetCallMsg). Answers "is
    // method-21 body a CharSerialize or a wrapper?" Gated on STELLAR_DIAGNOSTICS.
    private bool _diagContainer21Logged;
    private bool _diagContainer22Logged;

    private void DiagStubContainer(object stubCall, Type stubType, uint methodId)
    {
        if (!StellarDiagnostics.IsEnabled) return;

        if (methodId == WorldNtfMethodIds.SyncContainerData)
        {
            if (_diagContainer21Logged) return;
            _diagContainer21Logged = true;
        }
        else
        {
            if (_diagContainer22Logged) return;
            _diagContainer22Logged = true;
        }

        var shape = DescribeStubMsgShape(stubCall, stubType);
        _log.Info($"[Inventory] first WorldNtf container-sync method={methodId} body={shape}");
    }
}
