using System;
using System.Linq;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

internal static class CaptureRequestValidator
{
    public const int MaxLongSide = CaptureSizing.MaxLongSide;
    /// <summary>Total output pixels (64 MP = a 256 MB RGBA frame): above it a supersampled grab risks OOM even
    /// when the long side fits (4K at 4× = 133 MP). Lowered the same way as the long-side cap.</summary>
    public const long MaxPixels = CaptureSizing.MaxPixels;
    private static readonly char[] BadStemChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    private static readonly string[] ReservedStems =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Validates <paramref name="r"/>; the returned scale is the one a window-shaped capture really uses
    /// (<see cref="CaptureSizing.EffectiveScale"/>). A shaped capture (<see cref="CaptureRequest.Aspect"/>) gets its
    /// size from <see cref="CaptureSizing.OutputSize"/> with the REQUESTED scale instead.</summary>
    public static (int Scale, string? Error) Validate(CaptureRequest r, int screenW, int screenH, int maxTextureSize = MaxLongSide)
    {
        if (ShapeError(r) is { } shapeError) return (0, shapeError);
        if (r.Format == CaptureFormat.Jpg && r.JpgQuality is < 1 or > 100) return (0, "JPEG quality must be between 1 and 100.");
        if (string.IsNullOrWhiteSpace(r.Directory)) return (0, "No screenshot folder is set.");
        if (!IsValidStem(r.FileStem)) return (0, "The screenshot file name is not valid.");
        return (CaptureSizing.EffectiveScale(screenW, screenH, r.Scale, maxTextureSize), null);
    }

    /// <summary>The scale and shape rules alone (what <c>PlanSize</c> needs); null = valid.</summary>
    public static string? ShapeError(CaptureRequest r)
    {
        if (r.Scale is not (1 or 2 or 4)) return "Capture scale must be 1×, 2× or 4×.";
        if (r.Aspect is { IsValid: false }) return "The photo shape must be between 1:4 and 4:1.";
        return null;
    }

    private static bool IsValidStem(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return false;
        if (stem.IndexOfAny(BadStemChars) >= 0) return false;
        if (stem[^1] is '.' or ' ') return false;
        return !IsReservedStem(stem);
    }

    private static bool IsReservedStem(string stem)
    {
        // Windows reserves these names outright, and also with any trailing "." + suffix
        // (e.g. "CON.png" is just as invalid as bare "CON") — but not as a prefix of a
        // longer word (e.g. "CONcert" is fine).
        var dot = stem.IndexOf('.');
        var baseName = dot >= 0 ? stem[..dot] : stem;
        return ReservedStems.Any(reserved => string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase));
    }
}
