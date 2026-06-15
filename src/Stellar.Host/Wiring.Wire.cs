using System;
using System.Collections.Generic;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Hooks;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private void InstallWireAndStubProbes(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        // PandaWireTap owns the TCP recv hook (single owner); chat consumes
        // parsed envelopes via RegisterOnWireTap. PatchAll the wire-tap FIRST
        // so the recv hook is live before the chat probe subscribes.
        _wireTap = new PandaWireTap(log);
        _wireTap.PatchAll(PluginGuid);

        _chatProbe = new PandaChatProbe(log, _wireTap);
        _chatService!.AttachProbe(_chatProbe);
        _chatProbe.PatchAll();          // outbound ProxyCall + ZTcpClient.Send hooks
        _chatProbe.RegisterOnWireTap(); // subscribe to ChitChatNtf + all-Returns

        // Combat: sourced from Zservice.WorldNtfStub.OnCallStub (the C# stub
        // dispatcher), NOT from the TCP wire-tap. Live diagnostic confirmed
        // uuid=1664308034 WorldNtf traffic flows through OnCallStub but never
        // through ZTcpConnection.OnData on this build — the wire-tap path is a
        // dead end for combat data. The stub probe keeps its first-seen
        // diagnostic logging to surface unwired method IDs (e.g. 12293 =
        // legacy FullSkillEnd; 72/73 = unknown).
        _combatStubProbe = new PandaCombatStubProbe(_combatService!, log);

        // GrpcTeamNtfStubDispatcher owns the single HarmonyX postfix for
        // GrpcTeamNtfStub.OnCallStub. PandaPartyStubProbe registers its six
        // method IDs before Install() so the router is fully populated before
        // any packets arrive. Register-before-install ordering is required —
        // do NOT move Install() above RegisterWith().
        _partyStubProbe = new PandaPartyStubProbe(_partyService!, _wireTap!, log);
        _grpcTeamNtfDispatcher = new GrpcTeamNtfStubDispatcher(log);
        _partyStubProbe.RegisterWith(_grpcTeamNtfDispatcher);
        _grpcTeamNtfDispatcher.Install(PluginGuid);
        // Start attaches the wire-tap fallback if the stub wasn't found, and
        // always registers the GetTeamInfo_Ret return handler for login-roster.
        _partyStubProbe.Start(_grpcTeamNtfDispatcher.IsInstalled);

        InstallSocialDataProbe(log);

        // Combat + inventory both dispatch off Zservice.WorldNtfStub.OnCallStub.
        // WorldNtfStubDispatcher owns that single HarmonyX postfix; each probe
        // registers its method IDs before Install() so the router has all handlers
        // in place before any packets arrive. Register-before-install ordering is
        // required — do NOT move Install() above either RegisterWith() call.
        _worldNtfDispatcher = new WorldNtfStubDispatcher(log);
        _combatStubProbe.RegisterWith(_worldNtfDispatcher);
        _inventoryProbe!.RegisterWith(_worldNtfDispatcher);
        _worldNtfDispatcher.Install(PluginGuid);
    }

    // Social.GetSocialData reply: like GetTeamInfo_Ret it arrives as a Return on the login
    // connection (no service_uuid on the wire), so it routes through the same all-Returns
    // wire-tap path. The probe unwraps the GetSocialData_Ret envelope and pushes decoded
    // SocialSnapshots into SocialDataCache (the ISocialDataSink) — the SAME instance whose
    // read side feeds IEntityDetail.GetSocialSnapshot.
    private void InstallSocialDataProbe(BepInExPluginLog log)
    {
        _socialDataProbe = new PandaSocialDataProbe(_socialDataCache!, _wireTap!, log);
        _socialDataProbe.Start();
    }

    private void HookGameLifecycleMethods(BepInExPluginLog log, HarmonyGameMethodHooker hooker, Type gameType)
    {
        var dispatchers = new Dictionary<string, Action<object?, object?[]>>
        {
            // Capture the Game instance here (NOT from a per-frame Update hook) for the resolver probe.
            ["Init"]         = (inst, _) => { _gameInstance ??= inst; log.Info("[boot] *** Game.Init complete ***"); },
            ["OnLogin"]      = (_, _) => { _loggedIn = true; BeginSceneTransition(); _clientState!.RaiseLogin();  _inventoryProbe!.OnLifecycleAdvanced(); _harmonyBridge!.Publish("Panda.Core.LoginEvent", null); },
            ["OnLogout"]     = (_, _) => { _loggedIn = false; BeginSceneTransition(); _clientState!.RaiseLogout(); _harmonyBridge!.Publish("Panda.Core.LogoutEvent", null); },
            ["OnEnterScene"] = OnEnterScene,
            ["OnLeaveScene"] = (_, _) => { BeginSceneTransition(); _clientState!.RaiseSceneChanged(null); _harmonyBridge!.Publish("Panda.Core.OnLeaveSceneEvent", null); },
        };

        foreach (var methodName in GameLifecycleMethods)
        {
            if (!dispatchers.TryGetValue(methodName, out var callback))
            {
                continue;
            }
            hooker.PostfixAllOverloads(gameType, methodName, callback);
        }
    }
}
