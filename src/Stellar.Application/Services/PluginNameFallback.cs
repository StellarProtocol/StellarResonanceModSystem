using System;
using System.Text;

namespace Stellar.Application.Services;

/// <summary>
/// A readable plugin name derived from its assembly name, for a plugin that has never been constructed on this
/// install (its declared <c>Name</c> needs an instance): <c>StellarMahiruUtilityPlugin</c> → <c>Mahiru Utility</c>,
/// <c>Stellar.AutoNav</c> → <c>Auto Nav</c>. Display only — the plugin id stays the lower-cased assembly name.
/// </summary>
internal static class PluginNameFallback
{
    private const string Prefix = "Stellar";
    private const string Suffix = "Plugin";

    public static string FromAssemblyName(string assemblyName)
    {
        var s = assemblyName;
        var dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
        if (s.Length > Prefix.Length && s.StartsWith(Prefix, StringComparison.Ordinal)) s = s.Substring(Prefix.Length);
        if (s.Length > Suffix.Length && s.EndsWith(Suffix, StringComparison.Ordinal)) s = s.Substring(0, s.Length - Suffix.Length);

        var sb = new StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        var result = sb.ToString().Trim();
        return result.Length == 0 ? assemblyName : result;
    }
}
