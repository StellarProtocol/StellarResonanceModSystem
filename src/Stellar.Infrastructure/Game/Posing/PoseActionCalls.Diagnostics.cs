using System;
using System.Reflection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>Diagnostics only (STELLAR_DIAGNOSTICS=1): the game's registered-model count (<c>ZModelManager.modelDict_</c>)
/// — the leak measure of regression <c>clone-nre-male-null-ridetpl</c> (probe run 6: a failed photo copy left +1). Logged
/// after every <c>RecyclePhotoModel</c>; read by <see cref="PhotoCopyMaker"/> around each copy. Main thread.</summary>
internal sealed partial class PoseActionCalls
{
    private readonly SingletonAccess _models = new();
    private PropertyInfo? _modelDict, _dictCount;
    private Action<string>? _diagLog;

    /// <summary>Where the recycle count line goes (set by the diagnostics-only owner; null = silent).</summary>
    public void DiagnosticsTo(Action<string> log) => _diagLog = log;

    /// <summary><c>ZModelManager.Instance.modelDict_.Count</c>, or -1 when unreadable. Diagnostics only. Never throws.</summary>
    public int ModelCount()
    {
        try
        {
            if (_dictCount is null && !ResolveModelCount()) return -1;
            var mgr = _models.Get();
            var dict = mgr is null ? null : _modelDict!.GetValue(mgr);
            return dict is null ? -1 : Convert.ToInt32(_dictCount!.GetValue(dict));
        }
        catch { return -1; }
    }

    partial void OnRecycled()
    {
        if (!StellarDiagnostics.IsEnabled || _diagLog is null) return;
        _diagLog($"[Posing] photo copy recycled modelDict_={ModelCount()}");
    }

    private bool ResolveModelCount()
    {
        var t = _types.FindType(PhotoCopyMaker.ModelManagerType);
        if (t is null || !_models.Resolve(t)) return false;
        _modelDict = StellarInterop.FindPropertyUp(t, "modelDict_");
        _dictCount = _modelDict is null ? null : StellarInterop.FindPropertyUp(_modelDict.PropertyType, "Count");
        return _dictCount is not null;
    }
}
