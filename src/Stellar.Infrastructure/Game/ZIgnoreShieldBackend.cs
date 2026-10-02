using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// The game's own input-ignore mask (<c>ZIgnoreMgr.SetInputIgnore(ulong mask, bool, EIgnoreMaskSource, long)</c>) under
/// <c>EIgnoreMaskSource.EGm</c>: a source no game script uses, which coexists with the game's own <c>EPhoto</c> hold
/// (recon E). Never constructs <c>ZIgnoreMgr</c>. A second instance holds the scene freeze's pause input block
/// (<see cref="ForTimePause"/>): movement and combat only, under its own source, so neither release clears the other. Main
/// thread.
/// </summary>
internal sealed partial class ZIgnoreShieldBackend : IInputShieldBackend
{
    internal const string IgnoreMgrType = "Panda.ZGame.ZIgnoreMgr";
    internal const string MaskEnumType = "Panda.ZGame.EInputMask";
    internal const string SourceEnumType = "Panda.ZGame.EIgnoreMaskSource";
    internal const string SourceName = "EGm";
    /// <summary>The pause block's source: an <c>EIgnoreMaskSource</c> only the shop's pay web view uses, which cannot open
    /// or close by itself while the clock is stopped (sources index a fixed array; a value outside it is ignored — recon E).</summary>
    internal const string PauseSourceName = "EPayWebView";
    private const long CustomOperateId = 10003L;   // the game's default for this overload

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;
    private readonly string _sourceName;
    private readonly string[] _bits;
    private readonly SingletonAccess _mgr = new();
    private MethodInfo? _set;
    private object? _source;
    private ulong _mask;
    private bool _warned;

    public ZIgnoreShieldBackend(IGameTypeRegistry types, IPluginLog log) : this(types, log, SourceName, ShieldMask.Bits) { }

    private ZIgnoreShieldBackend(IGameTypeRegistry types, IPluginLog log, string sourceName, string[] bits)
    {
        _types = types;
        _log = log;
        _sourceName = sourceName;
        _bits = bits;
    }

    /// <summary>The pause input block: the local player's movement and combat (<see cref="ShieldMask.PauseBits"/>) under
    /// <see cref="PauseSourceName"/>. The camera stays free.</summary>
    public static ZIgnoreShieldBackend ForTimePause(IGameTypeRegistry types, IPluginLog log) =>
        new(types, log, PauseSourceName, ShieldMask.PauseBits);

    public bool SetShield(bool on)
    {
        if (!Resolve()) { WarnOnce("input shield unavailable on this client (ZIgnoreMgr / EGm not found)"); return false; }
        if (_mgr.Get() is not { } mgr) return false;
        try
        {
            _set!.Invoke(mgr, new object[] { _mask, on, _source!, CustomOperateId });
            OnShieldSet(on);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("input shield call failed: " + (ex.InnerException ?? ex).Message);
            return false;
        }
    }

    private bool Resolve()
    {
        if (_set is not null) return true;
        var t = _types.FindType(IgnoreMgrType);
        var maskT = _types.FindType(MaskEnumType);
        var srcT = _types.FindType(SourceEnumType);
        if (t is null || maskT is null || srcT is null || !_mgr.Resolve(t) || !Enum.IsDefined(srcT, _sourceName)) return false;
        _source = Enum.Parse(srcT, _sourceName);
        _mask = ShieldMask.Compose(n => Enum.IsDefined(maskT, n) ? Convert.ToInt32(Enum.Parse(maskT, n)) : null, _bits);
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

    partial void OnShieldSet(bool on);
}
