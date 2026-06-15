using System;
using System.Reflection;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for <see cref="PandaModuleEquipProbe"/>. Per-call
/// dispatch / result lines are gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>; the one-shot bridge-resolution
/// line fires unconditionally so the <c>hot-update-ready</c> /
/// <c>framework-load</c> scenario gates have evidence the Lua bridge resolved
/// even on a non-diagnostic run.
/// </summary>
internal sealed partial class PandaModuleEquipProbe
{
    // Throttle the failure log to once per minute when polled — the probe is
    // only consulted on user-initiated equip, but resolution may be retried.
    private int _failedResolutionAttempts;
    private const int ResolutionFailureLogEvery = 60;

    // Always-on one-shot: proves the Lua-bridge reflection targets resolved.
    private void OnResolutionSucceeded()
    {
        _log.Info(
            $"[Stellar][ModuleEquip] resolved equip bridge: Z.VMMgr.GetVM(\"{ModVmName}\")." +
            $"{AsyncEquipModFunc} via tolua# LuaState.mainState + DoString");
    }

    // Logs the first failure verbatim, then throttles repeats so a retried
    // resolve can't spam the log. Always on — these are rare and load-bearing
    // for the next dev iteration's in-world correction.
    private void OnResolutionFailure(string reason)
    {
        _failedResolutionAttempts++;
        if (!_resolutionFailureLogged)
        {
            _resolutionFailureLogged = true;
            _log.Warning($"[Stellar][ModuleEquip] bridge not resolved: {reason}");
            return;
        }
        if (_failedResolutionAttempts % ResolutionFailureLogEvery == 0)
        {
            _log.Warning($"[Stellar][ModuleEquip] bridge still not resolved ({_failedResolutionAttempts} attempts): {reason}");
        }
    }

    private void DiagDispatched(EquipRequest request)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (request.IsUninstall)
        {
            _log.Info($"[Stellar][ModuleEquip] UninstallMod(slot={request.SlotId}) dispatched");
        }
        else
        {
            _log.Info($"[Stellar][ModuleEquip] InstallMod(slot={request.SlotId}, uuid={request.ModuleUuid}) dispatched");
        }
    }

    private void DiagResult(EquipRequest request, Abstractions.Domain.Inventory.EquipResult result, long elapsedMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var verb = request.IsUninstall ? "UninstallMod" : "InstallMod";
        _log.Info($"[Stellar][ModuleEquip] {verb}(slot={request.SlotId}) result: {result} after {elapsedMs}ms");
    }

    // One-shot dump of the LuaInterface.LuaState API surface (methods + static
    // props), so the real "get main state" accessor can be identified — the
    // recon-assumed static GetMainState() is absent on this build. Gated on
    // diagnostics; greppable via [ModuleEquip][Api].
    private bool _luaApiDumped;

    private void DiagLuaStateApi(Type luaStateType)
    {
        if (!StellarDiagnostics.IsEnabled || _luaApiDumped) return;
        _luaApiDumped = true;
        try
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var m in luaStateType.GetMethods(Any))
            {
                var ps = m.GetParameters();
                var sig = string.Join(",", Array.ConvertAll(ps, p => p.ParameterType.Name));
                var gen = m.IsGenericMethodDefinition ? $"`{m.GetGenericArguments().Length}" : "";
                _log.Info($"[Stellar][ModuleEquip][Api] {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}{gen}({sig})");
            }
            foreach (var p in luaStateType.GetProperties(Any))
            {
                _log.Info($"[Stellar][ModuleEquip][Api] prop {(p.GetMethod?.IsStatic == true ? "static " : "")}{p.PropertyType.Name} {p.Name}");
            }
        }
        catch (Exception ex)
        {
            _log.Info($"[Stellar][ModuleEquip][Api] dump threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
