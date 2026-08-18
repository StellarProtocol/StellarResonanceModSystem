using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Localization;

public sealed class LocalizationEngineTests
{
    private const string En = "{\"a.b\":\"Hi\",\"n\":\"{0} DPS\"}";
    private const string Ja = "{\"a.b\":\"ヤア\",\"n\":\"{0} DPS\"}";

    private static LocalizationEngine New(FakeProbe probe, FakeConfigSection cfg)
        => new LocalizationEngine(cfg, probe, new FakeLog());

    [Fact]
    public void Resolves_active_then_english_then_key()
    {
        var e = New(new FakeProbe(), new FakeConfigSection());
        e.RegisterCatalog("p", "en", En);
        e.RegisterCatalog("p", "ja", "{\"a.b\":\"ヤア\"}");   // ja lacks "n"
        e.SetLanguageSetting("ja");
        Assert.Equal("ヤア", e.Resolve("p", "a.b"));   // active
        Assert.Equal("{0} DPS", e.Resolve("p", "n")); // en fallback (ja missing)
        Assert.Equal("z.z", e.Resolve("p", "z.z"));   // key literal (total miss)
    }

    [Fact]
    public void Format_applies_active_template_and_is_miss_safe()
    {
        var e = New(new FakeProbe(), new FakeConfigSection());
        e.RegisterCatalog("p", "en", En);
        e.RegisterCatalog("p", "ja", Ja);
        e.SetLanguageSetting("en");
        Assert.Equal("1234 DPS", e.ResolveFormat("p", "n", new object[] { 1234 }));
        // A missing key returns the key literal unformatted (no FormatException).
        Assert.Equal("no.such", e.ResolveFormat("p", "no.such", new object[] { 1 }));
    }

    [Fact]
    public void Follow_mode_tracks_probe_and_persists()
    {
        var probe = new FakeProbe { SupportedLanguage = "th" };
        var cfg = new FakeConfigSection();
        var e = New(probe, cfg);
        e.RegisterCatalog("p", "en", En);
        e.RegisterCatalog("p", "th", "{\"a.b\":\"สวัสดี\"}");
        Assert.Equal("follow", e.LanguageSetting);       // default
        Assert.Equal("th", e.ActiveLanguage);            // resolved from probe
        Assert.Equal("สวัสดี", e.Resolve("p", "a.b"));
        e.SetLanguageSetting("en");
        Assert.Equal("Hi", e.Resolve("p", "a.b"));
        Assert.Equal("en", cfg.Get<string>("language", null));  // persisted
        Assert.True(cfg.SaveCount >= 1);
    }

    [Fact]
    public void SetLanguage_fires_event_only_on_change()
    {
        var e = New(new FakeProbe(), new FakeConfigSection());
        int fired = 0;
        e.LanguageChanged += () => fired++;
        e.SetLanguageSetting("ja");
        e.SetLanguageSetting("ja");
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Invalid_setting_is_ignored()
    {
        var e = New(new FakeProbe(), new FakeConfigSection());
        e.SetLanguageSetting("klingon");
        Assert.Equal("follow", e.LanguageSetting);
    }
}
