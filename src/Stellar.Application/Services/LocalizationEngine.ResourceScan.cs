using System;

namespace Stellar.Application.Services;

/// <summary>
/// Maps an embedded-resource name to a supported language code. A catalog resource is named
/// <c>&lt;anything&gt;Lang.&lt;code&gt;.json</c> (the default MSBuild logical name
/// <c>Stellar.Infrastructure.Lang.en.json</c>, or a namespace-independent <c>Lang.en.json</c>
/// when the plugin sets an explicit <c>LogicalName</c>). Both match by the shared suffix.
/// </summary>
internal static class LocalizationResourceScan
{
    private static readonly string[] Codes = { "en", "ja", "th", "id" };

    /// <summary>The supported code if <paramref name="name"/> ends with
    /// <c>Lang.&lt;code&gt;.json</c> (ordinal, case-insensitive); otherwise <c>null</c>.</summary>
    public static string? CodeFromResourceName(string name)
    {
        foreach (var c in Codes)
            if (name.EndsWith("Lang." + c + ".json", StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }
}
