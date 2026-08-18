using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Localizes this plugin's own UI text. Resolves a stable structured key to the active UI
/// language, falling back to English and then to the key literal. Scoped to the calling
/// plugin's own catalog (like <see cref="IPluginLog"/>) — keys never collide across plugins.
/// Ship four <c>Lang/&lt;code&gt;.json</c> catalogs (<c>en</c>, <c>ja</c>, <c>th</c>, <c>id</c>)
/// as <c>EmbeddedResource</c> in your plugin; the framework auto-discovers them at load.
/// </summary>
public interface ILocalization
{
    /// <summary>Active UI language code: <c>"en"</c>, <c>"ja"</c>, <c>"th"</c> or <c>"id"</c>.</summary>
    string Language { get; }

    /// <summary>Raised after the active language changes (live switch). Rebuild any UI text you
    /// cached; text resolved at draw-time (a <c>Func&lt;string&gt;</c> label) needs no handler.</summary>
    event Action LanguageChanged;

    /// <summary>Resolve <paramref name="key"/> to the active-language string
    /// (active → English → the key literal if unknown).</summary>
    string T(string key);

    /// <summary>Resolve <paramref name="key"/> to a template and <c>string.Format</c> it with
    /// <paramref name="args"/>. Templates carry positional placeholders (<c>{0}</c>) so other
    /// languages may reorder them. A missing key returns the key literal, unformatted.</summary>
    string TFormat(string key, params object[] args);
}
