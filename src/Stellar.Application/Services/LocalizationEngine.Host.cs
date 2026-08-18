using System.IO;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// <see cref="ILocalizationHost"/> half of the engine: enumerates a plugin assembly's embedded
/// <c>Lang/&lt;code&gt;.json</c> resources, registers each, and returns the plugin's scoped façade.
/// </summary>
internal sealed partial class LocalizationEngine : ILocalizationHost
{
    public ILocalization RegisterPlugin(string ns, Assembly asm)
    {
        var count = 0;
        foreach (var res in asm.GetManifestResourceNames())
        {
            var code = LocalizationResourceScan.CodeFromResourceName(res);
            if (code is null) continue;
            using var s = asm.GetManifestResourceStream(res);
            if (s is null) continue;
            using var reader = new StreamReader(s);
            RegisterCatalog(ns, code, reader.ReadToEnd());
            count++;
        }
        if (count == 0) _log.Debug($"[Stellar][i18n] '{ns}' shipped no Lang/*.json catalogs");
        return new PluginLocalization(this, ns);
    }
}
