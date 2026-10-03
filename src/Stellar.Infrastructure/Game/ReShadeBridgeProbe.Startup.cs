using System;
using System.IO;
namespace Stellar.Infrastructure.Game;

/// <summary>PROBE ONLY — part (a): bind, wait for ReShade's load, log the snapshot, round-trip effects off/on.</summary>
internal sealed partial class ReShadeBridgeProbe
{
    private const double LoadTimeoutS = 60, EnabledTimeoutS = 5;
    private int _requestFrame;

    private Bridge B => _bridge ?? throw new InvalidOperationException("bridge not bound");

    private bool StepBind()
    {
        var mod = GetModuleHandleW(AddonName);
        _log.Info(Tag + $"addon module={(mod == IntPtr.Zero ? "NOT LOADED" : "0x" + mod.ToString("x"))}");
        if (mod == IntPtr.Zero) { Finish(); return false; }
        _bridge = new Bridge(mod);
        _log.Info(Tag + $"version={B.Version()} ready={B.Ready()} is_loading={B.IsLoading()} enabled={B.GetEnabled()} snapshotFrames={B.SnapshotFrames()}");
        Directory.CreateDirectory(_outDir);
        return true;
    }

    private bool StepWaitNotLoading()
    {
        if (B.IsLoading() == 0 && B.SnapshotFrames() > 0)
        {
            _log.Info(Tag + $"is_loading=0 after {StepSeconds * 1000:F0} ms");
            return true;
        }
        if (StepSeconds < LoadTimeoutS) return false;
        _log.Warning(Tag + $"still loading after {LoadTimeoutS:F0} s (is_loading={B.IsLoading()} snapshotFrames={B.SnapshotFrames()}); continuing");
        return true;
    }

    private bool StepLogSnapshot()
    {
        var list = B.Techniques();
        _log.Info(Tag + $"techniques={list.Count}: " + string.Join(", ", list.ConvertAll(t => t.Name + (t.On != 0 ? "*" : ""))));
        return true;
    }

    private bool StepRequestEnabled(int on)
    {
        _requestFrame = B.SnapshotFrames();
        _log.Info(Tag + $"set_enabled({on}) queued={B.SetEnabled(on)} (snapshotFrame={_requestFrame})");
        return true;
    }

    // "Later frame" = two snapshots after the request, so a present has certainly applied it.
    private bool StepCheckEnabled(int want)
    {
        var frames = B.SnapshotFrames();
        var got = B.GetEnabled();
        if (frames >= _requestFrame + 2 && got == want)
        {
            _log.Info(Tag + $"get_enabled={got} (want {want}) OK after {frames - _requestFrame} presents / {StepSeconds * 1000:F0} ms");
            return true;
        }
        if (StepSeconds < EnabledTimeoutS) return false;
        _log.Warning(Tag + $"get_enabled={got} (want {want}) NOT confirmed after {EnabledTimeoutS:F0} s, presents={frames - _requestFrame}");
        return true;
    }
}
