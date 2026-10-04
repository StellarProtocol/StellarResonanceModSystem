using System;
using System.Globalization;

namespace Stellar.Application.Services;

/// <summary>The value form <c>IReShadeUniforms.SetUniformOverride</c> accepts (the bridge's own rule, checked before
/// sending so a value held for a not-yet-loaded bridge is never a bad one): 1 to 16 comma-separated finite numbers, or
/// <c>true</c>/<c>false</c> per item, whitespace around items ignored.</summary>
internal static class UniformOverrideValue
{
    internal const int MaxComponents = 16;

    internal static bool IsValid(string value)
    {
        var parts = value.Split(',');
        if (parts.Length > MaxComponents) return false;
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Equals("true", StringComparison.OrdinalIgnoreCase) || part.Equals("false", StringComparison.OrdinalIgnoreCase)) continue;
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                return false;
        }
        return true;
    }
}
