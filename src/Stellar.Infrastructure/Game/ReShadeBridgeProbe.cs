using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Game;

/// <summary>PROBE ONLY (branch probe/reshade-bridge, never merged) — spec 2026-10-03-photo-studio-reshade-design.md § 8.
/// Armed by RESHADEPROBE in stellar_perf.flags. A per-frame state machine (at most ONE bridge action per frame, so each
/// offscreen render_effects call gets its own frame): 30 s after the world is active, bind the bridge add-on, wait for
/// ReShade to finish loading, round-trip effects off/on through the present-thread queue, then for 1× and 4× retry an
/// offscreen render every ~0.5 s until ReShade really draws (techniques counted) and pixels change — the warm-up cost of
/// a first photo at a new size; repeat with DisplayDepth on, then switch it off and confirm. PNGs →
/// stellar/screenshots/reshadeprobe/. Ends with "[RsProbe] done".</summary>
internal sealed partial class ReShadeBridgeProbe
{
    private const string Tag = "[RsProbe] ";

    private readonly IPluginLog _log;
    private readonly string _outDir;
    private readonly List<Func<bool>> _steps = new();
    private readonly Stopwatch _stepClock = new();
    private Bridge? _bridge;
    private float _t;
    private int _step;
    private bool _finished;
    private bool _depthEverOn, _effectsEverOff;

    public ReShadeBridgeProbe(IPluginLog log, string gameMiniDir)
    {
        _log = log;
        _outDir = Path.Combine(gameMiniDir, "stellar", "screenshots", "reshadeprobe");
        BuildSteps();
    }

    public static bool Armed => PerfControls.Flag("RESHADEPROBE");

    private double StepSeconds => _stepClock.Elapsed.TotalSeconds;

    /// <summary>Main-thread tick. Runs the current step once; a step returns true when it is complete.</summary>
    public void Tick(float dt)
    {
        if (_finished) return;
        _t += dt;
        if (_t < 30f) return;
        if (!_stepClock.IsRunning) _stepClock.Start();
        try
        {
            if (!_steps[_step]()) return;
            _step++;
            _stepClock.Restart();
            if (_step >= _steps.Count) Finish();
        }
        catch (Exception ex)
        {
            _log.Warning(Tag + $"step {_step} threw: " + ex);
            Finish();
        }
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        RestoreBestEffort();
        _log.Info(Tag + "done");
    }

    // Best effort: whatever the probe switched must not outlive it, also after a step threw. Queued; the add-on applies
    // them on a later present.
    private void RestoreBestEffort()
    {
        if (_bridge is null) return;
        if (_depthEverOn)
        {
            try { _log.Info(Tag + $"cleanup: set_technique({DepthTechnique},0) queued={_bridge.SetTechnique(DepthTechnique, 0)}"); }
            catch (Exception ex) { _log.Warning(Tag + "cleanup depth off threw: " + ex.Message); }
        }
        if (_effectsEverOff)
        {
            try { _log.Info(Tag + $"cleanup: set_enabled(1) queued={_bridge.SetEnabled(1)}"); }
            catch (Exception ex) { _log.Warning(Tag + "cleanup effects on threw: " + ex.Message); }
        }
    }

    private void BuildSteps()
    {
        _steps.Add(StepBind);
        _steps.Add(StepWaitNotLoading);
        _steps.Add(StepLogSnapshot);
        _steps.Add(() => StepRequestEnabled(0));
        _steps.Add(() => StepCheckEnabled(0));
        _steps.Add(() => StepRequestEnabled(1));
        _steps.Add(() => StepCheckEnabled(1));
        AddSeries("fx", 1);
        AddSeries("fx", 4);
        _steps.Add(() => StepRequestDepth(1));
        _steps.Add(StepAwaitDepthOn);
        AddSeries("depth", 1);
        AddSeries("depth", 4);
        _steps.Add(() => StepRequestDepth(0));
        _steps.Add(StepAwaitDepthOff);
    }

    private void AddSeries(string label, int scale)
    {
        RenderSeries? series = null;
        _steps.Add(() => (series ??= new RenderSeries(this, label, scale)).Step());
    }
}
