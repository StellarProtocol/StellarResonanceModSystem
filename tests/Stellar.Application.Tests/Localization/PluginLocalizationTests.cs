using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Localization;

public sealed class PluginLocalizationTests
{
    private static LocalizationEngine Engine()
        => new LocalizationEngine(new FakeConfigSection(), new FakeProbe(), new FakeLog());

    [Fact]
    public void Facade_is_namespace_isolated()
    {
        var e = Engine();
        e.RegisterCatalog("a", "en", "{\"k\":\"A\"}");
        e.RegisterCatalog("b", "en", "{\"k\":\"B\"}");
        ILocalization a = new PluginLocalization(e, "a");
        ILocalization b = new PluginLocalization(e, "b");
        Assert.Equal("A", a.T("k"));
        Assert.Equal("B", b.T("k"));
    }

    [Fact]
    public void Facade_reports_active_language_and_format()
    {
        var e = Engine();
        e.RegisterCatalog("p", "en", "{\"n\":\"{0} DPS\"}");
        ILocalization loc = new PluginLocalization(e, "p");
        Assert.Equal("en", loc.Language);
        Assert.Equal("42 DPS", loc.TFormat("n", 42));
    }
}
