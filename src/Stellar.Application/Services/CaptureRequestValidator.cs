using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

internal static class CaptureRequestValidator
{
    public const int MaxLongSide = 16384;
    private static readonly char[] BadStemChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    public static (int Scale, string? Error) Validate(CaptureRequest r, int screenW, int screenH)
    {
        if (r.Scale is not (1 or 2 or 4)) return (0, "Capture scale must be 1×, 2× or 4×.");
        if (string.IsNullOrWhiteSpace(r.Directory)) return (0, "No screenshot folder is set.");
        if (string.IsNullOrWhiteSpace(r.FileStem) || r.FileStem.IndexOfAny(BadStemChars) >= 0)
            return (0, "The screenshot file name is not valid.");
        var scale = r.Scale;
        var longSide = Math.Max(screenW, screenH);
        while (scale > 1 && longSide * scale > MaxLongSide) scale /= 2;
        return (scale, null);
    }
}
