using System;
using Stellar.Abstractions.Services;
using Stellar.Wire;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game.Protobuf;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Routes the WorldNtf dungeon ready-check pushes (method 70
/// <c>NotifyAllMemberReady</c> open/close, method 71 <c>NotifyCaptainReady</c>
/// per-member response) from the shared <see cref="WorldNtfStubDispatcher"/>
/// into <see cref="IPartyEventSink"/>. Mirrors <see cref="PandaPartyStubProbe"/>
/// but on the WorldNtf service.
///
/// <para>
/// Call <see cref="RegisterWith"/> before <see cref="WorldNtfStubDispatcher.Install"/>
/// so the router has the handlers in place before any packets arrive. The probe
/// installs no hook of its own — the dispatcher owns the single OnCallStub postfix.
/// </para>
/// </summary>
internal sealed class PandaReadyCheckProbe
{
    private readonly IPartyLiveSink _sink;
    private readonly IPluginLog      _log;

    public PandaReadyCheckProbe(IPartyLiveSink sink, IPluginLog log)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _log  = log  ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Subscribes methods 70 + 71 to the Lua-stub dispatcher. Call before <c>Install</c>.
    /// Ready-check is Lua-only — it flows through ZLuaStub, not the C# WorldNtfStub.</summary>
    public void RegisterWith(WorldNtfLuaStubDispatcher dispatcher)
    {
        dispatcher.Register(WorldNtfMethodIds.NotifyAllMemberReady, Dispatch);
        dispatcher.Register(WorldNtfMethodIds.NotifyCaptainReady,   Dispatch);
    }

    // Runs on the network receive thread. Decodes and enqueues; the sink marshals
    // to the Unity main thread. Never throws across the IL2CPP boundary.
    private void Dispatch(uint methodId, byte[] payload)
    {
        var span = (ReadOnlySpan<byte>)payload;
        try
        {
            switch (methodId)
            {
                case WorldNtfMethodIds.NotifyCaptainReady:
                    if (NotifyReadyCheckReader.TryReadCaptainReady(span, out var charId, out var name, out var ready))
                        _sink.EnqueueReadyCheckResponse(charId, name, ready);
                    break;
                case WorldNtfMethodIds.NotifyAllMemberReady:
                    if (NotifyReadyCheckReader.TryReadAllMemberReady(span, out var isOpen))
                        _sink.EnqueueReadyCheckPhase(isOpen);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[ReadyCheck] dispatch methodId={methodId} threw: {ex.Message}");
        }
    }
}
