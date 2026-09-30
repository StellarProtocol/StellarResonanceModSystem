using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Rendering;

/// <summary>One VolumeParameter write: component type name, parameter field, managed value.</summary>
internal sealed record ParamWrite(string Component, string Field, object Value);

/// <summary>Marker value: the applier loads this PNG into a Texture2D.</summary>
internal sealed record LutPath(string Path);

/// <summary>Marker value: the applier parses this name into the parameter's enum type.</summary>
internal sealed record EnumName(string Name);

/// <summary>
/// What changed between the last applied plan and the next one: the writes to make (only fields whose value
/// differs, plus every field of a group that just turned on) and the components whose group turned OFF (the
/// applier drops their overrides so the game's own value returns). A component that stays on is never cleared.
/// </summary>
internal sealed record LookPlanDiff(IReadOnlyList<ParamWrite> Writes, IReadOnlyList<string> ClearComponents)
{
    public bool IsEmpty => Writes.Count == 0 && ClearComponents.Count == 0;
}

/// <summary>Pure mapping from LookSettings to ZRenderPipeline parameter writes. Names from docs/recon/photo-studio-render-recon.md.</summary>
internal static class LookParameterPlan
{
    private const string Ns = "Bokura.Rendering.";
    public const string DofComponent = Ns + "ZDofVolume";
    public const string ColorComponent = Ns + "ZColorAdjustmentVolume";
    public const string WhiteBalanceComponent = Ns + "ZWhiteBalanceVolume";
    public const string LutComponent = Ns + "ZColorLookupVolume";
    public const string BloomComponent = Ns + "ZBloomVolume";
    public const string VignetteComponent = Ns + "ZUnityVignetteVolume";
    public const string FilmGrainComponent = Ns + "ZFilmGrainVolume";
    /// <summary>The one field the focus-tracking path writes.</summary>
    public const string FocusField = "FocusDistance";

    public static string ComponentFor(LookGroups g) => g switch
    {
        LookGroups.Dof => DofComponent,
        LookGroups.Color => ColorComponent,
        LookGroups.WhiteBalance => WhiteBalanceComponent,
        LookGroups.Lut => LutComponent,
        LookGroups.Bloom => BloomComponent,
        LookGroups.Vignette => VignetteComponent,
        LookGroups.FilmGrain => FilmGrainComponent,
        _ => throw new ArgumentOutOfRangeException(nameof(g)),
    };

    /// <summary>The two LUT strip layouts the pipeline accepts (16³ and 32³).</summary>
    public static bool IsLutSize(int width, int height) => (width, height) is (256, 16) or (1024, 32);

    /// <summary>The single write the focus-tracking path makes.</summary>
    public static ParamWrite FocusWrite(float distance) => new(DofComponent, FocusField, distance);

    public static IReadOnlyList<ParamWrite> Build(LookSettings s) => Build(s, static _ => true);

    /// <summary>As <see cref="Build(LookSettings)"/>, but a LUT <paramref name="lutUsable"/> rejects drops the WHOLE
    /// Lut group (no contribution write against the game's own texture).</summary>
    public static IReadOnlyList<ParamWrite> Build(LookSettings s, Func<string, bool> lutUsable)
    {
        var w = new List<ParamWrite>();
        if (s.Dof is { } d) AddDof(w, d);
        if (s.Color is { } c) AddColor(w, c);
        if (s.WhiteBalance is { } wb) AddAll(w, WhiteBalanceComponent, ("Enabled", true), ("Temperature", wb.Temperature), ("Tint", wb.Tint));
        if (s.Lut is { } l && lutUsable(l.FilePath)) AddAll(w, LutComponent, ("texture", new LutPath(l.FilePath)), ("contribution", l.Contribution));
        // Recon (DXVK): only the _UE pair is live; plain intensity > 0 keeps ZBloomVolume.IsActive() true.
        if (s.Bloom is { } b) AddAll(w, BloomComponent, ("Enabled", true), ("intensity", Math.Max(b.Intensity, 0.01f)), ("intensity_UE", b.Intensity), ("threshold_UE", b.Threshold));
        if (s.Vignette is { } v) AddAll(w, VignetteComponent, ("intensity", v.Intensity), ("smoothness", v.Smoothness));
        if (s.FilmGrain is { } f) AddAll(w, FilmGrainComponent, ("Enabled", true), ("Intensity", f.Intensity), ("Response", f.Response));
        return w;
    }

    /// <summary>Diffs two settings (no LUT validation — both LUTs count as usable). See the write-list overload.</summary>
    public static LookPlanDiff Diff(LookSettings? prev, LookSettings? next) =>
        Diff(prev is null ? Array.Empty<ParamWrite>() : Build(prev), next is null ? Array.Empty<ParamWrite>() : Build(next));

    /// <summary>
    /// Diffs the writes last applied (<paramref name="prev"/>) against the next plan. A component absent from
    /// <paramref name="next"/> is cleared; a component that lost only SOME fields is cleared and fully rewritten
    /// (never happens with today's fixed per-group field sets, handled so a future plan cannot leak an override).
    /// </summary>
    public static LookPlanDiff Diff(IReadOnlyList<ParamWrite> prev, IReadOnlyList<ParamWrite> next)
    {
        var prevValues = new Dictionary<(string, string), object>(prev.Count);
        foreach (var w in prev) prevValues[(w.Component, w.Field)] = w.Value;
        var nextKeys = new HashSet<(string, string)>();
        foreach (var w in next) nextKeys.Add((w.Component, w.Field));

        var clear = new List<string>();
        foreach (var (component, field) in prevValues.Keys)
        {
            if (nextKeys.Contains((component, field)) || clear.Contains(component)) continue;
            clear.Add(component);   // group turned off, or (defensively) lost a field
        }
        var writes = new List<ParamWrite>();
        foreach (var w in next)
        {
            var rewriteAll = clear.Contains(w.Component);
            if (rewriteAll || !prevValues.TryGetValue((w.Component, w.Field), out var old) || !Equals(old, w.Value)) writes.Add(w);
        }
        return new LookPlanDiff(writes, clear);
    }

    // Recon: ZDofVolume's default blurType (DepthBlur) blurs nothing; Bokeh is the live path.
    private static void AddDof(List<ParamWrite> w, DofLook d) =>
        AddAll(w, DofComponent, ("Enabled", true), ("blurType", new EnumName("Bokeh")), (FocusField, d.FocusDistance), ("Aperture", d.Aperture), ("FocalLength", d.FocalLength));

    private static void AddColor(List<ParamWrite> w, ColorLook c) =>
        AddAll(w, ColorComponent, ("postExposure", c.PostExposure), ("contrast", c.Contrast), ("saturation", c.Saturation), ("colorFilter", c.Filter));

    private static void AddAll(List<ParamWrite> w, string component, params (string Field, object Value)[] fields)
    {
        foreach (var (field, value) in fields) w.Add(new ParamWrite(component, field, value));
    }
}
