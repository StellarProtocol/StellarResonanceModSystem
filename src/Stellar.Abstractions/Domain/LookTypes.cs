using System;

namespace Stellar.Abstractions.Domain;

/// <summary>Look groups, as flags.</summary>
[Flags]
public enum LookGroups
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>Depth of field.</summary>
    Dof = 1,
    /// <summary>Exposure / contrast / saturation / filter.</summary>
    Color = 2,
    /// <summary>Temperature / tint.</summary>
    WhiteBalance = 4,
    /// <summary>Lookup-table grading.</summary>
    Lut = 8,
    /// <summary>Bloom.</summary>
    Bloom = 16,
    /// <summary>Vignette.</summary>
    Vignette = 32,
    /// <summary>Film grain.</summary>
    FilmGrain = 64,
}

/// <summary>Linear RGB color, 0–1 per channel.</summary>
/// <param name="R">Red.</param><param name="G">Green.</param><param name="B">Blue.</param>
public readonly record struct RgbColor(float R, float G, float B)
{
    /// <summary>White (no filter).</summary>
    public static RgbColor White => new(1f, 1f, 1f);
}

/// <summary>Depth of field.</summary>
public sealed record DofLook
{
    /// <summary>Focus distance in meters.</summary>
    public float FocusDistance { get; init; } = 3f;
    /// <summary>Aperture f-stop.</summary>
    public float Aperture { get; init; } = 2.8f;
    /// <summary>Focal length in mm.</summary>
    public float FocalLength { get; init; } = 50f;
    /// <summary>When true the framework keeps focus on the local player's character.</summary>
    public bool FocusOnLocalPlayer { get; init; }
}

/// <summary>Color adjustments (Unity ranges).</summary>
public sealed record ColorLook
{
    /// <summary>Post exposure in EV.</summary>
    public float PostExposure { get; init; }
    /// <summary>Contrast −100..100.</summary>
    public float Contrast { get; init; }
    /// <summary>Saturation −100..100.</summary>
    public float Saturation { get; init; }
    /// <summary>Multiplicative color filter.</summary>
    public RgbColor Filter { get; init; } = RgbColor.White;
}

/// <summary>White balance.</summary>
public sealed record WhiteBalanceLook
{
    /// <summary>Temperature −100..100.</summary>
    public float Temperature { get; init; }
    /// <summary>Tint −100..100.</summary>
    public float Tint { get; init; }
}

/// <summary>LUT grading from a PNG strip on disk.</summary>
public sealed record LutLook
{
    /// <summary>Absolute path to a 256×16 or 1024×32 PNG strip.</summary>
    public string FilePath { get; init; } = "";
    /// <summary>Blend 0–1.</summary>
    public float Contribution { get; init; } = 1f;
}

/// <summary>Bloom.</summary>
public sealed record BloomLook
{
    /// <summary>Intensity ≥ 0.</summary>
    public float Intensity { get; init; } = 1f;
    /// <summary>Threshold ≥ 0.</summary>
    public float Threshold { get; init; } = 0.9f;
}

/// <summary>Vignette.</summary>
public sealed record VignetteLook
{
    /// <summary>Intensity 0–1.</summary>
    public float Intensity { get; init; } = 0.3f;
    /// <summary>Smoothness 0–1.</summary>
    public float Smoothness { get; init; } = 0.4f;
}

/// <summary>Film grain.</summary>
public sealed record FilmGrainLook
{
    /// <summary>Intensity 0–1.</summary>
    public float Intensity { get; init; } = 0.25f;
    /// <summary>Luminance response 0–1.</summary>
    public float Response { get; init; } = 0.8f;
}

/// <summary>A complete look. A null group leaves the game's own value untouched.</summary>
public sealed record LookSettings
{
    /// <summary>Depth of field.</summary>
    public DofLook? Dof { get; init; }
    /// <summary>Color adjustments.</summary>
    public ColorLook? Color { get; init; }
    /// <summary>White balance.</summary>
    public WhiteBalanceLook? WhiteBalance { get; init; }
    /// <summary>LUT.</summary>
    public LutLook? Lut { get; init; }
    /// <summary>Bloom.</summary>
    public BloomLook? Bloom { get; init; }
    /// <summary>Vignette.</summary>
    public VignetteLook? Vignette { get; init; }
    /// <summary>Film grain.</summary>
    public FilmGrainLook? FilmGrain { get; init; }
    /// <summary>True while the player is playing (not in photo mode): DoF and film grain are stripped.</summary>
    public bool PlayMode { get; init; }
}

/// <summary>Which look groups this client can render.</summary>
/// <param name="Supported">Supported groups.</param>
public sealed record LookCapabilities(LookGroups Supported);
