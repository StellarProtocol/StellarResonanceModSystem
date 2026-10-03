namespace Stellar.Infrastructure.Game;

/// <summary>PROBE ONLY — part (c): DisplayDepth on/off through the present-thread queue, confirmed from the snapshot.</summary>
internal sealed partial class ReShadeBridgeProbe
{
    private const string DepthTechnique = "DisplayDepth";
    private const double DepthTimeoutS = 10;

    private bool StepRequestDepth(int on)
    {
        if (on != 0) _depthEverOn = true;
        _log.Info(Tag + $"set_technique({DepthTechnique},{on}) queued={B.SetTechnique(DepthTechnique, on)}");
        return true;
    }

    private bool StepAwaitDepthOn()
    {
        var state = B.TechniqueState(DepthTechnique);
        if (state == 1)
        {
            _log.Info(Tag + $"{DepthTechnique} ON confirmed after {StepSeconds * 1000:F0} ms (is_loading={B.IsLoading()})");
            return true;
        }
        if (StepSeconds < DepthTimeoutS) return false;
        _log.Warning(Tag + $"{DepthTechnique} ON NOT confirmed after {DepthTimeoutS:F0} s (state={state} is_loading={B.IsLoading()})");
        return true;
    }

    private bool StepAwaitDepthOff()
    {
        var state = B.TechniqueState(DepthTechnique);
        if (state == 0)
        {
            _log.Info(Tag + $"{DepthTechnique} OFF confirmed after {StepSeconds * 1000:F0} ms");
            return true;
        }
        if (StepSeconds < DepthTimeoutS) return false;
        _log.Error(Tag + $"ERROR DisplayDepth may still be on (state={state} is_loading={B.IsLoading()} after {DepthTimeoutS:F0} s)");
        return true;
    }
}
