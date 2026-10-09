using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Localization;

/// <summary>
/// PINNED regression (framework 2.21.0 — never weaken): with the language setting <c>follow</c>, the framework read
/// the game's client language at wiring time, BEFORE the game's LocalizationMgr initialised, got the static's
/// default <c>0</c> (zh_Hans) and latched it for the whole session — measured <c>client language index=0
/// designLanguage=True</c> at boot on an en_TH (11) and a ja (2) client. Every follow user got English and the
/// Chinese design-label fallback was armed. A pre-ready 0 must never latch; the real value must, with ONE
/// LanguageChanged; an explicit setting is unaffected; a genuine zh_Hans client still latches 0.
/// </summary>
public sealed class ClientLanguageLatchTests
{
    private const string En = "{\"a.b\":\"Hi\"}";
    private const string Ja = "{\"a.b\":\"ヤア\"}";

    private static (ClientLanguageLatch latch, FakeLanguageReader reader) NewLatch()
    {
        var reader = new FakeLanguageReader();
        return (new ClientLanguageLatch(reader, new FakeLog()), reader);
    }

    private static LocalizationEngine NewEngine(ClientLanguageLatch latch, FakeConfigSection cfg)
    {
        var e = new LocalizationEngine(cfg, latch, new FakeLog());
        e.RegisterCatalog("p", "en", En);
        e.RegisterCatalog("p", "ja", Ja);
        return e;
    }

    [Fact]
    public void Early_default_zero_is_not_latched_and_the_real_value_is()
    {
        var (latch, reader) = NewLatch();
        var e = NewEngine(latch, new FakeConfigSection());
        var fired = 0;
        e.LanguageChanged += () => fired++;

        reader.Index = 0;                          // game static before LocalizationMgr init (the bug's read)
        latch.TryLatchNonDefault("hook-install");
        Assert.Equal(ClientLanguageLatch.Unknown, latch.CurrentLanguageIndex);
        Assert.False(latch.IsDesignLanguage);      // never decided from the provisional 0
        Assert.Equal("en", e.ActiveLanguage);      // provisional English, not latched
        Assert.Equal("Hi", e.Resolve("p", "a.b"));

        reader.Index = 2;                          // the game sets ja
        latch.OnLanguageSet("game-setter");
        latch.DrainPendingChange();                // framework tick
        Assert.Equal(2, latch.CurrentLanguageIndex);
        Assert.Equal("ja", e.ActiveLanguage);
        Assert.Equal("ヤア", e.Resolve("p", "a.b"));
        Assert.Equal(1, fired);

        latch.OnLanguageSet("char-select");        // backstop re-signal with the same value: no second event
        latch.DrainPendingChange();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Non_default_value_latches_opportunistically()
    {
        var (latch, reader) = NewLatch();
        reader.Index = 11;                         // en_TH, already set by the game
        latch.TryLatchNonDefault("hook-install");
        Assert.Equal(11, latch.CurrentLanguageIndex);
        Assert.Equal("en", latch.SupportedLanguage);
        Assert.False(latch.IsDesignLanguage);
    }

    [Fact]
    public void Genuine_simplified_chinese_client_latches_zero_after_the_game_signal()
    {
        var (latch, reader) = NewLatch();
        var e = NewEngine(latch, new FakeConfigSection());
        var fired = 0;
        e.LanguageChanged += () => fired++;

        reader.Index = 0;
        latch.TryLatchNonDefault("hook-install");
        Assert.Equal(ClientLanguageLatch.Unknown, latch.CurrentLanguageIndex);
        latch.OnLanguageSet("char-select");
        latch.DrainPendingChange();
        Assert.Equal(0, latch.CurrentLanguageIndex);
        Assert.True(latch.IsDesignLanguage);
        Assert.Equal("en", e.ActiveLanguage);      // no zh catalog: follow stays English
        Assert.Equal(0, fired);                    // en → en is not a change
    }

    [Fact]
    public void Explicit_setting_is_unaffected_by_the_client_latch()
    {
        var (latch, reader) = NewLatch();
        var cfg = new FakeConfigSection();
        cfg.Set("language", "ko");
        var e = NewEngine(latch, cfg);
        var fired = 0;
        e.LanguageChanged += () => fired++;

        reader.Index = 2;
        latch.OnLanguageSet("game-setter");
        latch.DrainPendingChange();
        Assert.Equal("ko", e.ActiveLanguage);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void In_game_switch_after_latch_is_tracked()
    {
        var (latch, reader) = NewLatch();
        var e = NewEngine(latch, new FakeConfigSection());
        var fired = 0;
        e.LanguageChanged += () => fired++;

        reader.Index = 2;
        latch.OnLanguageSet("game-setter");
        latch.DrainPendingChange();
        reader.Index = 1;
        latch.OnLanguageSet("game-SetLanguage");
        latch.DrainPendingChange();
        Assert.Equal("en", e.ActiveLanguage);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Language_reads_never_touch_the_game()
    {
        var (latch, reader) = NewLatch();
        reader.Index = 2;
        for (var i = 0; i < 10; i++) _ = latch.SupportedLanguage;
        _ = latch.IsDesignLanguage;
        Assert.Equal(0, reader.Reads);             // hot path (every T()) is a field read, not reflection
        Assert.Equal(ClientLanguageLatch.Unknown, latch.CurrentLanguageIndex);   // and a read never latches
    }

    [Fact]
    public void Unreadable_game_type_stays_unknown()
    {
        var (latch, reader) = NewLatch();
        reader.Index = -1;
        latch.OnLanguageSet("char-select");
        Assert.Equal(ClientLanguageLatch.Unknown, latch.CurrentLanguageIndex);
        Assert.Equal("en", latch.SupportedLanguage);
    }

    [Fact]
    public void Changed_is_raised_on_the_tick_drain_not_inside_the_game_signal()
    {
        // The signals run inside the game's language setter (Harmony postfix); subscribers (window rebuilds) must not.
        var (latch, reader) = NewLatch();
        var e = NewEngine(latch, new FakeConfigSection());
        var fired = 0;
        e.LanguageChanged += () => fired++;

        reader.Index = 2;
        latch.OnLanguageSet("game-setter");
        latch.OnLanguageSet("game-SetLanguage");   // the game calls both
        Assert.Equal(0, fired);                    // nothing raised inside the signal
        Assert.Equal(2, latch.CurrentLanguageIndex);   // but the value is latched already

        latch.DrainPendingChange();
        Assert.Equal(1, fired);
        latch.DrainPendingChange();                // nothing pending: a flag check, no second raise
        Assert.Equal(1, fired);
        Assert.Equal(1, reader.Reads - 1);         // drains read nothing from the game (2 signal reads total)
    }
}
