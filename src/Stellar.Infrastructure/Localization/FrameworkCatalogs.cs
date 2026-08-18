using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Stellar.Infrastructure.Localization;

/// <summary>
/// Reads Infrastructure's own embedded <c>Lang/*.json</c> — the framework's UI catalog, registered
/// under the reserved <c>"stellar.framework"</c> namespace. Mirrors the embedded-resource pattern of
/// <c>EmbeddedAssetProvider</c> / <c>LauncherIcons</c>. Resource names are
/// <c>Stellar.Infrastructure.Lang.&lt;code&gt;.json</c> (the MSBuild logical-name default).
/// </summary>
internal static class FrameworkCatalogs
{
    private const string Prefix = "Stellar.Infrastructure.Lang.";

    public static IEnumerable<(string code, string json)> Read()
    {
        var asm = typeof(FrameworkCatalogs).Assembly;
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith(Prefix, System.StringComparison.Ordinal) || !res.EndsWith(".json", System.StringComparison.Ordinal))
                continue;
            var code = res.Substring(Prefix.Length, res.Length - Prefix.Length - ".json".Length);
            using var s = asm.GetManifestResourceStream(res);
            if (s is null) continue;
            using var reader = new StreamReader(s);
            yield return (code, reader.ReadToEnd());
        }
    }
}
