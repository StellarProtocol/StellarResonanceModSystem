using System;
using System.Reflection;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Rendering;

/// <summary>Member resolution for the render-quality levers. A resolved lever is cached for good; a lever whose
/// game TYPE is not loaded yet is retried (the type registry memoizes the miss until an assembly loads); a type that
/// loaded without the member is a permanent miss (a loaded type never grows members).</summary>
internal sealed partial class ZRenderQualityBackend
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    internal const string PipelineType = "Bokura.Rendering.ZRenderPipeline";
    internal const string PipelineAssetType = "Bokura.Rendering.ZRenderPipelineAsset";
    internal const string QualityGradeType = "Panda.Utility.Quality.QualityGradeSetting";
    internal const string ShadowPassType = "Bokura.Rendering.ZShadowCastPass";

    private enum Lever { Unresolved, Resolved, Missing }

    private Lever _scaleState, _taaState, _shadowState;
    private PropertyInfo? _pipelineAsset;   // ZRenderPipeline.asset (static)
    private PropertyInfo? _renderScale;     // ZRenderPipelineAsset.renderScale
    private PropertyInfo? _enableAA;        // QualityGradeSetting.EnableAA (static)
    private MethodInfo? _applyEnableAA;     // QualityGradeSetting.applyEnableAA(bool) (private static)
    private MethodInfo? _shadowInstance;    // ZScriptableRendererPassSingleton<ZShadowCastPass>.s_Instance / Instance getter
    private PropertyInfo? _shadowSettings;  // ZShadowCastPass.shadowSettings (getter only)
    private MemberAccess _shadowRes, _cascades, _soft;

    private bool ResolveScale() => Resolve(ref _scaleState, () =>
    {
        var pipeline = _types.FindType(PipelineType);
        var asset = _types.FindType(PipelineAssetType);
        if (pipeline is null || asset is null) return null;
        _pipelineAsset = pipeline.GetProperty("asset", AnyStatic);
        _renderScale = asset.GetProperty("renderScale", AnyInstance);
        return _pipelineAsset is not null && _renderScale is not null;
    }, "render scale");

    private bool ResolveTaa() => Resolve(ref _taaState, () =>
    {
        var q = _types.FindType(QualityGradeType);
        if (q is null) return null;
        _enableAA = q.GetProperty("EnableAA", AnyStatic);
        _applyEnableAA = StellarInterop.FindMethod(q, "applyEnableAA", 1);
        return _enableAA is not null && _applyEnableAA is { IsStatic: true };
    }, "TAA");

    private bool ResolveShadows() => Resolve(ref _shadowState, () =>
    {
        var pass = _types.FindType(ShadowPassType);
        if (pass is null) return null;
        // s_Instance first: reading it never constructs the pass (Instance may create it lazily).
        _shadowInstance = StaticGetter(pass, "s_Instance") ?? StaticGetter(pass, "Instance");
        _shadowSettings = StellarInterop.FindPropertyUp(pass, "shadowSettings");
        if (_shadowInstance is null || _shadowSettings is null) return false;
        var settingsType = _shadowSettings.PropertyType;
        _shadowRes = MemberAccess.Find(settingsType, "shadowmapResolution");
        _cascades = MemberAccess.Find(settingsType, "cascadesCount");
        _soft = MemberAccess.Find(settingsType, "supportsSoftShadows");
        return _shadowRes.IsValid && _cascades.IsValid && _soft.IsValid;
    }, "shadows");

    // resolve() → null = the type isn't loaded yet (retry later), true/false = final.
    private bool Resolve(ref Lever state, Func<bool?> resolve, string name)
    {
        if (state != Lever.Unresolved) return state == Lever.Resolved;
        bool? ok;
        try { ok = resolve(); }
        catch { ok = false; }
        if (ok is null) return false;
        state = ok.Value ? Lever.Resolved : Lever.Missing;
        if (ok.Value) _log.Info(Tag + name + " lever resolved.");
        else _log.Warning(Tag + name + " unavailable on this client (game member not found).");
        return ok.Value;
    }

    private object? LiveShadowSettings()
    {
        var pass = _shadowInstance!.Invoke(null, null);
        return pass is null ? null : _shadowSettings!.GetValue(pass);
    }

    private static MethodInfo? StaticGetter(Type t, string name) =>
        StellarInterop.FindPropertyUp(t, name)?.GetGetMethod(nonPublic: true) is { IsStatic: true } g ? g : null;

    /// <summary>A member that is a field on some builds and a property on others (IL2CPP interop surfaces fields as
    /// properties; a managed build has real fields). Field first, then property — MahiruUtility's rule.</summary>
    internal readonly struct MemberAccess
    {
        private readonly FieldInfo? _field;
        private readonly PropertyInfo? _property;
        private MemberAccess(FieldInfo? field, PropertyInfo? property) { _field = field; _property = property; }

        public bool IsValid => _field is not null || (_property is { CanRead: true, CanWrite: true });

        public static MemberAccess Find(Type t, string name)
        {
            var f = StellarInterop.FindFieldUp(t, name);
            return f is { IsStatic: false } ? new MemberAccess(f, null) : new MemberAccess(null, StellarInterop.FindPropertyUp(t, name));
        }

        public object? Get(object target) => _field is not null ? _field.GetValue(target) : _property!.GetValue(target);

        public void Set(object target, object value)
        {
            if (_field is not null) _field.SetValue(target, value);
            else _property!.SetValue(target, value);
        }
    }
}
