using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// The game's own input-ignore mask (<c>ZIgnoreMgr.SetInputIgnore(ulong mask, bool, EIgnoreMaskSource, long)</c>) under
/// <c>EIgnoreMaskSource.EGm</c>: a source no game script uses, which coexists with the game's own <c>EPhoto</c> hold
/// (recon E). Never constructs <c>ZIgnoreMgr</c>. The ONE owner of that source (qa M-4, 2026-10-03): it holds the union of
/// the free camera's mask (<see cref="ShieldMask.Bits"/>) and the scene freeze's pause block (<see cref="ShieldMask.PauseBits"/>)
/// — the pause block used to sit under <c>EPayWebView</c>, which the shop's payment callback may clear wholesale. The game
/// COUNTS each bit per source (<c>SetInputIgnore</c> → <c>AddIgnore(…, ignoreLayer = ignore ? +1 : −1)</c> adds to a per-bit,
/// per-source counter; a bit is ignored while its count is above 0 — release_3.7 ISIL), so a change moves only the
/// difference (<see cref="ShieldMask.Transition"/>): the bits no longer wanted get −1, the newly wanted ones +1, and every bit
/// of this source stays at count 0 or 1. Main thread.
/// </summary>
internal sealed partial class ZIgnoreShieldBackend : IInputShieldBackend
{
    internal const string IgnoreMgrType = "Panda.ZGame.ZIgnoreMgr";
    internal const string MaskEnumType = "Panda.ZGame.EInputMask";
    internal const string SourceEnumType = "Panda.ZGame.EIgnoreMaskSource";
    internal const string SourceName = "EGm";
    private const long CustomOperateId = 10003L;   // the game's default for this overload

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;
    private readonly SingletonAccess _mgr = new();
    private MethodInfo? _set;
    private object? _source;
    private ulong _cameraMask, _pauseMask;
    private ulong _applied;
    private bool _warned;

    public ZIgnoreShieldBackend(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types;
        _log = log;
    }

    public bool Apply(bool camera, bool pause)
    {
        if (!Resolve()) { WarnOnce("input shield unavailable on this client (ZIgnoreMgr / EGm not found)"); return false; }
        if (_mgr.Get() is not { } mgr) return false;
        var next = (camera ? _cameraMask : 0UL) | (pause ? _pauseMask : 0UL);
        var (clear, add) = ShieldMask.Transition(_applied, next);
        try
        {
            if (clear != 0) { Set(mgr, clear, false); _applied &= ~clear; }
            if (add != 0) { Set(mgr, add, true); _applied |= add; }
            OnShieldSet(camera, pause, clear, add);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("input shield call failed: " + (ex.InnerException ?? ex).Message);
            return false;
        }
    }

    public void Forget() => _applied = 0;

    private void Set(object mgr, ulong bits, bool on) => _set!.Invoke(mgr, new object[] { bits, on, _source!, CustomOperateId });

    private bool Resolve()
    {
        if (_set is not null) return true;
        var t = _types.FindType(IgnoreMgrType);
        var maskT = _types.FindType(MaskEnumType);
        var srcT = _types.FindType(SourceEnumType);
        if (t is null || maskT is null || srcT is null || !_mgr.Resolve(t) || !Enum.IsDefined(srcT, SourceName)) return false;
        _source = Enum.Parse(srcT, SourceName);
        Func<string, int?> valueOf = n => Enum.IsDefined(maskT, n) ? Convert.ToInt32(Enum.Parse(maskT, n)) : null;
        _cameraMask = ShieldMask.Compose(valueOf, ShieldMask.Bits);
        _pauseMask = ShieldMask.Compose(valueOf, ShieldMask.PauseBits);
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var p = m.GetParameters();
            if (m.Name == "SetInputIgnore" && p.Length == 4 && p[0].ParameterType == typeof(ulong) && p[2].ParameterType == srcT)
            {
                _set = m;
                break;
            }
        }
        return _set is not null;
    }

    private void WarnOnce(string message)
    {
        if (_warned) return;
        _warned = true;
        _log.Warning("[FreeCam] " + message);
    }

    partial void OnShieldSet(bool camera, bool pause, ulong cleared, ulong added);
}
