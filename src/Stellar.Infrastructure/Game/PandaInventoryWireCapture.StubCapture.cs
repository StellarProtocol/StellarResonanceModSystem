using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Dispatcher-based capture for <see cref="PandaInventoryWireCapture"/>. Subscribes
/// to <see cref="WorldNtfStubDispatcher"/> for WorldNtf method 21
/// (<c>SyncContainerData</c>, full inventory sync) and method 22
/// (<c>SyncContainerDirtyData</c>, incremental deltas). The dispatcher owns
/// the single HarmonyX postfix on <c>Zservice.WorldNtfStub.OnCallStub</c>,
/// reads the stub header once with cached accessors, and routes subscribed
/// method IDs here with pre-extracted payload bytes.
///
/// <para>On method 21 the handler decodes the payload into a
/// <c>Zproto.CharSerialize</c> and latches it into
/// <c>_capturedCharSerialize</c>. On method 22 it parses the incremental
/// delta and applies it to the maintained equipped set.</para>
///
/// <para>Decode strategy: byte-parse the raw payload into the WRAPPER
/// <c>Zproto.WorldNtf.Types.SyncContainerData</c> and read its
/// <c>VData</c> (the fresh <c>CharSerialize</c>) FIRST — this mirrors the
/// two proven external reference decoders. The wrapper type is resolved on
/// the first invocation via <c>AccessTools.TypeByName</c>. Only if
/// byte extraction/parse fails do we fall back to the bare CharSerialize
/// parser. See <c>PandaInventoryWireCapture.StubCapture.ByteParse.cs</c> for the
/// wrapper parse state and <c>PandaInventoryWireCapture.StubCapture.Decode.cs</c>
/// for the fallback paths.</para>
/// </summary>
internal sealed partial class PandaInventoryWireCapture
{
    private bool _stubPayloadFailLogged;

    // Lazily-resolved CharSerialize protobuf parser (byte[] fallback path).
    private MethodInfo? _charSerializeParseFrom; // static ParseFrom(byte[]) or Parser.ParseFrom(byte[])
    private object? _charSerializeParserInstance; // null for static ParseFrom; the Parser for instance ParseFrom
    private bool _parseFromResolved;

    /// <summary>
    /// Subscribes to <paramref name="dispatcher"/> for WorldNtf method 21
    /// (<c>SyncContainerData</c>) and method 22
    /// (<c>SyncContainerDirtyData</c>). Both registrations route to the
    /// same <see cref="HandleWorldNtf"/> handler, which distinguishes them
    /// by method ID. Call before <see cref="WorldNtfStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWith(WorldNtfStubDispatcher dispatcher)
    {
        dispatcher.Register(WorldNtfMethodIds.SyncContainerData,      HandleWorldNtf); // 21
        dispatcher.Register(WorldNtfMethodIds.SyncContainerDirtyData, HandleWorldNtf); // 22
        _state.CaptureHookActive = true;
        // Drop any pull-based candidate list built before registration so the
        // next resolution attempt uses the capture-only fast path.
        _pullReader.ResetCandidateCache();
        _log.Info("[Inventory] registered with WorldNtfStubDispatcher for SyncContainerData(21) + SyncContainerDirtyData(22)");
    }

    // Dispatcher callback. Called by WorldNtfStubDispatcher after it has
    // confirmed uuid==WorldNtf and methodId∈{21,22}. Payload bytes are the
    // pre-extracted GetCallData() span — identical to what ExtractStubPayloadBytes
    // produced when the probe owned its own hook. Runs on the network receive
    // thread; must not throw.
    private void HandleWorldNtf(uint methodId, byte[] payload)
    {
        try
        {
            if (methodId == WorldNtfMethodIds.SyncContainerDirtyData)
            {
                HandleDirtyContainerFromBytes(payload);
                return;
            }

            // Method 21 (SyncContainerData): full sync. Self-gear decode runs
            // first — it is a pure byte-walk (no IL2CPP reflection), so a
            // wrapper-resolution miss in the object decode below can't block it.
            // Own try/catch so a gear-decode surprise can never skip the module
            // latch + reseed below (review finding: shared catch coupled them).
            try { DecodeSelfGearFromSync(payload); }
            catch (Exception ex) { _log.Warning($"[Inventory][Gear] self-gear decode threw: {ex.GetType().Name}: {ex.Message}"); }

            // Full sync → latch CharSerialize and reseed the equipped set that
            // subsequent method-22 deltas will mutate.
            var decoded = DecodeCharSerializeFromBytes(payload);
            if (decoded is null) return;
            _state.CapturedCharSerialize = decoded;
            ReseedEquippedFromSync(decoded);
            _pullReader.OnCharSerializeCaptured();
        }
        catch (Exception ex)
        {
            try { _log.Warning($"[Inventory] HandleWorldNtf({methodId}) threw: {ex.GetType().Name}: {ex.Message}"); }
            catch { /* logging itself failed — give up silently */ }
        }
    }

    // Decodes the LOCAL player's equipped gear straight off the method-21 wire
    // bytes (SyncContainerData.VData(1) = the bare CharSerialize bytes) via the
    // pure Stellar.Wire GearInstanceReader, then pushes the result to the
    // Application-side cache. Full syncs are authoritative: the sink REPLACES
    // its list on every call (evict-and-replace, never merge).
    private void DecodeSelfGearFromSync(byte[] payload)
    {
        var charSerialize = ReadLenDelimitedField(payload, fieldNum: 1); // SyncContainerData.VData
        if (charSerialize is null) return;

        var gear = GearInstanceReader.Read(charSerialize);
        _gearSink.OnGearSync(gear);
        DiagGearDecoded(gear.Count);
    }

    // Method 22 entry from the dispatcher. The raw stub payload bytes are the
    // same as ExtractStubPayloadBytes produced, so the field walk (field1→field1)
    // used by ExtractDirtyDeltaBytes is identical — just skip the GetCallData
    // reflection now that the bytes are pre-supplied.
    private void HandleDirtyContainerFromBytes(byte[] wire)
    {
        if (wire.Length == 0) return;

        var vData = ReadLenDelimitedField(wire, fieldNum: 1);   // SyncContainerDirtyData.VData
        if (vData is null) return;

        var buffer = ReadLenDelimitedField(vData, fieldNum: 1); // BufferStream.Buffer
        if (buffer is null || buffer.Length == 0) return;

        var slotDelta = Protobuf.ContainerDirtyDeltaReader.Read(buffer);
        if (!slotDelta.Touched) return;

        ApplyModSlotDelta(slotDelta);
    }

    // Decodes a method-21 SyncContainerData payload into a CharSerialize.
    //
    // Priority (identical to the stub-call path in the old HandleStubCall):
    //   1. byte-parse payload into the SyncContainerData WRAPPER and read VData.
    //   2. byte-parse payload directly as CharSerialize (last resort; wrong shape
    //      on this build, kept in case a future build delivers a bare CharSerialize).
    // The GetCallMsg object path (DecodeFromCallMsg) is no longer available
    // because the dispatcher does not expose the stub object.
    private object? DecodeCharSerializeFromBytes(byte[] payload)
    {
        if (payload.Length == 0) return null;

        var fromWrapper = DecodeFromWrapperBytesOnly(payload);
        if (fromWrapper is not null) return fromWrapper;

        return DecodeFromPayloadBytesDirectly(payload);
    }

    // Decode path 1: parse payload into SyncContainerData wrapper, read VData.
    // Uses the same cached _wrapperParseFrom / _wrapperVDataReader that
    // ByteParse.cs sets up, but resolves the wrapper type by name instead of
    // via GetCallMsg (which requires the stub object).
    private object? DecodeFromWrapperBytesOnly(byte[] payload)
    {
        EnsureWrapperParseResolvedByName();
        if (_wrapperParseFrom is null || _wrapperVDataReader is null || _wrapperParseArgType is null) return null;

        object? arg = ToParseArg(payload, _wrapperParseArgType);
        if (arg is null) return null;

        object? wrapper;
        try { wrapper = _wrapperParseFrom.Invoke(_wrapperParserInstance, new[] { arg }); }
        catch (Exception ex)
        {
            if (!_wrapperParseFailLogged)
            {
                _wrapperParseFailLogged = true;
                _log.Warning($"[Inventory] SyncContainerData wrapper ParseFrom threw: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
        if (wrapper is null) return null;

        return _wrapperVDataReader.Read(wrapper);
    }

    // One-shot wrapper type resolution using the known IL2CPP type name rather
    // than GetCallMsg() (which requires the live stub object). The wrapper is
    // Zproto.WorldNtf.Types.SyncContainerData — the same type ByteParse.cs
    // would resolve via the stub call's GetCallMsg(). Reuses and sets all the
    // same fields (_wrapperParseFrom, _wrapperParserInstance, _wrapperParseArgType,
    // _wrapperParseArgCtor, _wrapperVDataReader, _wrapperResolved) that the
    // stub-call path in ByteParse.cs would set.
    private void EnsureWrapperParseResolvedByName()
    {
        if (_wrapperResolved) return;
        _wrapperResolved = true;

        // Try the IL2CPP-interop flat name first, then the nested-type separator.
        var wrapperType = _typeRegistry.FindType("Zproto.WorldNtf.Types.SyncContainerData")
            ?? PandaInventoryPullReader.FindTypeByShortName("SyncContainerData");
        if (wrapperType is null)
        {
            try
            {
                wrapperType = HarmonyLib.AccessTools.TypeByName("Zproto.WorldNtf.Types.SyncContainerData")
                    ?? HarmonyLib.AccessTools.TypeByName("Zproto.WorldNtf+Types+SyncContainerData");
            }
            catch { /* AccessTools may throw on missing types */ }
        }
        if (wrapperType is null)
        {
            _log.Warning("[Inventory] wrapper type Zproto.WorldNtf.Types.SyncContainerData not found; byte-parse path disabled");
            return;
        }

        var charSerializeType = ResolveCharSerializeType();
        if (charSerializeType is null)
        {
            _log.Warning("[Inventory] CharSerialize type not resolved; wrapper byte-parse path disabled");
            return;
        }

        ResolveWrapperParseHandle(wrapperType);
        if (_wrapperParseFrom is null) return;

        if (_wrapperParseArgType is not null && _wrapperParseArgType != typeof(byte[]))
        {
            _wrapperParseArgCtor = ResolveIl2CppByteArrayCtor(_wrapperParseArgType);
        }

        _wrapperVDataReader = PandaInventoryPullReader.TryBuildInstanceCharSerializeMemberReader(wrapperType, charSerializeType);
    }

    // Reseeds the maintained equipped set from a freshly-captured full-sync
    // CharSerialize. Builds a fresh dict off the authoritative ModSlots and
    // publishes it atomically (copy-on-write), so a full sync always resets the
    // baseline that method-22 deltas then mutate. Resolution of the ModSlots
    // property handles happens lazily inside ReadEquippedSlots; before the
    // resolver has run them this returns empty, which is the correct seed.
    private void ReseedEquippedFromSync(object charSerialize)
    {
        _pullReader.EnsureModSlotHandles(charSerialize.GetType());
        try
        {
            var slots = _pullReader.ReadEquippedSlots(charSerialize);
            _state.PublishEquippedSnapshot(new System.Collections.Generic.Dictionary<int, long>(slots));
        }
        catch
        {
            // A bad reseed must never wipe a good snapshot — leave it untouched.
        }
    }

    // Decode path 2 (last resort): parse payload bytes directly as CharSerialize.
    // Wrong shape on this build (payload is the wrapper), but kept for builds
    // where the wrapper type can't be resolved.
    private object? DecodeFromPayloadBytesDirectly(byte[] payload)
    {
        EnsureParseFromResolved();
        if (_charSerializeParseFrom is null) return null;

        try { return _charSerializeParseFrom.Invoke(_charSerializeParserInstance, new object[] { payload }); }
        catch (Exception ex)
        {
            if (!_stubPayloadFailLogged)
            {
                _stubPayloadFailLogged = true;
                _log.Warning($"[Inventory] CharSerialize.ParseFrom threw: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
    }
}
