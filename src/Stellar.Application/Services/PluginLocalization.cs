using System;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Per-namespace <see cref="ILocalization"/> façade over the shared <see cref="LocalizationEngine"/>.
/// One is minted per plugin (namespace = plugin GUID) and one for the framework
/// (namespace = <c>"stellar.framework"</c>). Delegates every call to the engine, scoped to its namespace.
/// </summary>
internal sealed class PluginLocalization : ILocalization
{
    private readonly LocalizationEngine _engine;
    private readonly string _ns;

    public PluginLocalization(LocalizationEngine engine, string ns)
    {
        _engine = engine;
        _ns = ns;
    }

    public string Language => _engine.ActiveLanguage;

    public event Action LanguageChanged
    {
        add => _engine.LanguageChanged += value;
        remove => _engine.LanguageChanged -= value;
    }

    public string T(string key) => _engine.Resolve(_ns, key);

    public string TFormat(string key, params object[] args) => _engine.ResolveFormat(_ns, key, args);
}
