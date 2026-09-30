using System;
using System.Linq;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

internal static class CaptureRequestValidator
{
    public const int MaxLongSide = 16384;
    private static readonly char[] BadStemChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    private static readonly string[] ReservedStems =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static (int Scale, string? Error) Validate(CaptureRequest r, int screenW, int screenH)
    {
        if (r.Scale is not (1 or 2 or 4)) return (0, "Capture scale must be 1×, 2× or 4×.");
        if (r.Format == CaptureFormat.Jpg && r.JpgQuality is < 1 or > 100) return (0, "JPEG quality must be between 1 and 100.");
        if (string.IsNullOrWhiteSpace(r.Directory)) return (0, "No screenshot folder is set.");
        if (!IsValidStem(r.FileStem)) return (0, "The screenshot file name is not valid.");
        var scale = r.Scale;
        var longSide = Math.Max(screenW, screenH);
        while (scale > 1 && longSide * scale > MaxLongSide) scale /= 2;
        return (scale, null);
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
