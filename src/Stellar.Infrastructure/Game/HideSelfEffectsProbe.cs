using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game;

/// <summary>PROBE ONLY (branch probe/hide-self-effects, never merged). Armed by a HIDEPROBE line in stellar_perf.flags.
/// Once per process, 20 s after the world is active, every 5 s: (0) census of ZEffectManager.EffectDict caster fields;
/// (1) SetEffectVisible(uid,false) on every effect with a caster; (2) read IsVisible back, then show them again;
/// (3) SetEntityShow(1/14,false) + every non-zero ETakePhotos hold count; (4) counts, then show 1/14; (5) counts + census.</summary>
internal sealed class HideSelfEffectsProbe
{
    private const string Tag = "[HideProbe] ";
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;
    private readonly Func<long> _localUuid;
    private readonly SingletonAccess _fxMgr = new(), _cam = new(), _ents = new();
    private readonly List<long> _touched = new();
    private float _t;
    private int _step;

    public HideSelfEffectsProbe(IGameTypeRegistry types, IPluginLog log, Func<long> localUuid)
    {
        _types = types;
        _log = log;
        _localUuid = localUuid;
    }

    public static bool Armed => PerfControls.Flag("HIDEPROBE");

    public void Tick(float dt)
    {
        if (_step >= 6) return;
        _t += dt;
        if (_t < 20f + _step * 5f) return;
        try { Run(_step); }
        catch (Exception ex) { _log.Warning(Tag + "step " + _step + " threw: " + (ex.InnerException ?? ex)); }
        _step++;
    }

    private void Run(int step)
    {
        switch (step)
        {
            case 0: Census(); break;
            case 1: SetAllVisible(false); break;
            case 2: ReadBack(); SetAllVisible(true); break;
            case 3: EntityShow(1, false); EntityShow(14, false); HoldCounts("after-hide"); break;
            case 4: HoldCounts("before-show"); EntityShow(1, true); EntityShow(14, true); break;
            default: HoldCounts("after-show"); Census(); break;
        }
    }

    private object? Mgr() => _fxMgr.Resolve(_types.FindType("Panda.ZEffect.ZEffectManager")) ? _fxMgr.Get() : null;

    private IEnumerable<(long Uid, object Fx)> Effects(object mgr)
    {
        var dict = StellarInterop.FindPropertyUp(mgr.GetType(), "EffectDict")!.GetValue(mgr);
        var keys = dict!.GetType().GetProperty("Keys")!.GetValue(dict);
        var en = keys!.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)!.Invoke(keys, null)!;
        var move = en.GetType().GetMethod("MoveNext")!;
        var cur = en.GetType().GetProperty("Current")!;
        var get = mgr.GetType().GetMethod("GetEffect", new[] { typeof(long) })!;
        var list = new List<(long, object)>();
        while (move.Invoke(en, null) is true)
        {
            var uid = Convert.ToInt64(cur.GetValue(en));
            if (get.Invoke(mgr, new object[] { uid }) is { } fx) list.Add((uid, fx));
        }
        return list;
    }

    private static object? Ctx(object fx) => StellarInterop.FindPropertyUp(fx.GetType(), "Context")?.GetValue(fx);
    private static object? Prop(object o, string name) => StellarInterop.FindPropertyUp(o.GetType(), name)?.GetValue(o);

    private string Kind(long uuid)
    {
        if (uuid == 0) return "none";
        if (uuid == _localUuid()) return "self";
        var low = uuid & 0xFFFF;
        return low == 640 ? "player" : low is 64 or 32832 ? "monster" : "other(low=" + low + ")";
    }

    private void Census()
    {
        if (Mgr() is not { } mgr) { _log.Warning(Tag + "ZEffectManager not available"); return; }
        var n = 0;
        foreach (var (uid, fx) in Effects(mgr))
        {
            if (Ctx(fx) is not { } c) { _log.Info(Tag + $"fx uid={uid} context=null"); continue; }
            long from = Convert.ToInt64(Prop(c, "FromUuid")), belong = Convert.ToInt64(Prop(c, "BelongUuid"));
            if (n++ < 300)
                _log.Info(Tag + $"fx uid={uid} from={from}({Kind(from)}) belong={belong}({Kind(belong)}) type={Prop(c, "Type")} " +
                          $"visible={Prop(c, "IsVisible")} addr={Prop(c, "Addr")}");
        }
        _log.Info(Tag + $"census total={n} local={_localUuid()}");
    }

    private void SetAllVisible(bool visible)
    {
        if (Mgr() is not { } mgr) return;
        var set = mgr.GetType().GetMethod("SetEffectVisible", new[] { typeof(long), typeof(bool) })!;
        if (!visible) _touched.Clear();
        foreach (var (uid, fx) in Effects(mgr))
        {
            if (!visible && (Ctx(fx) is not { } c || (Convert.ToInt64(Prop(c, "FromUuid")) == 0 && Convert.ToInt64(Prop(c, "BelongUuid")) == 0))) continue;
            if (visible && !_touched.Contains(uid)) continue;
            set.Invoke(mgr, new object[] { uid, visible });
            if (!visible) _touched.Add(uid);
        }
        _log.Info(Tag + $"SetEffectVisible({visible}) on {_touched.Count} effects");
    }

    private void ReadBack()
    {
        if (Mgr() is not { } mgr) return;
        int hidden = 0, still = 0;
        foreach (var (uid, fx) in Effects(mgr))
        {
            if (!_touched.Contains(uid) || Ctx(fx) is not { } c) continue;
            if (Prop(c, "IsVisible") is false) hidden++; else still++;
        }
        _log.Info(Tag + $"readback hidden={hidden} stillVisible={still} of {_touched.Count}");
    }

    private void EntityShow(int type, bool show)
    {
        var t = _types.FindType("Panda.ZGame.CameraFrameCtrl");
        if (!_cam.Resolve(t) || _cam.Get() is not { } ctrl) { _log.Warning(Tag + "CameraFrameCtrl not available"); return; }
        t!.GetMethod("SetEntityShow", Any, null, new[] { typeof(int), typeof(bool) }, null)!.Invoke(ctrl, new object[] { type, show });
        _log.Info(Tag + $"SetEntityShow({type},{show})");
    }

    private void HoldCounts(string label)
    {
        var t = _types.FindType("Panda.ZGame.ZEntityMgr");
        if (!_ents.Resolve(t) || _ents.Get() is not { } mgr) return;
        var m = StellarInterop.FindMethod(t!, "getHideCount", 2)!;
        var ps = m.GetParameters();
        var source = Enum.ToObject(ps[1].ParameterType, 4);   // EVisibleSource.ETakePhotos
        foreach (var v in Enum.GetValues(ps[0].ParameterType))
            if (m.Invoke(mgr, new[] { v, source }) is int n && n != 0)
                _log.Info(Tag + $"{label} hideType={v}({Convert.ToInt32(v)}) count={n}");
    }
}
