using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class PerfPrefsPerPluginTests
{
    // Minimal in-memory IConfigSection for testing PerfPrefs persistence.
    private sealed class MemSection : IConfigSection
    {
        public readonly Dictionary<string, object?> Store = new();
        public T? Get<T>(string key, T? def) => Store.TryGetValue(key, out var v) && v is T t ? t : def;
        public void Set<T>(string key, T value) => Store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
    }

    [Fact]
    public void Set_and_get_per_plugin_rate_round_trips()
    {
        var cfg = new MemSection();
        var prefs = new PerfPrefs(cfg);
        prefs.SetPluginRate("stellar.exchangebuyer", 240);
        Assert.Equal(240, prefs.GetPluginRate("stellar.exchangebuyer"));
        Assert.Equal(240, cfg.Get<int>("plugin_rate.stellar.exchangebuyer", 0));
    }

    [Fact]
    public void Zero_rate_means_follow_global()
    {
        var cfg = new MemSection();
        var prefs = new PerfPrefs(cfg);
        Assert.Equal(0, prefs.GetPluginRate("nope"));
    }

    [Fact]
    public void Self_control_flag_round_trips()
    {
        var cfg = new MemSection();
        var prefs = new PerfPrefs(cfg);
        prefs.SetPluginSelfControl("stellar.exchangebuyer", true);
        Assert.True(prefs.GetPluginSelfControl("stellar.exchangebuyer"));
        Assert.True(cfg.Get<bool>("plugin_selfcontrol.stellar.exchangebuyer", false));
    }

    [Fact]
    public void Setting_a_value_pushes_to_the_configure_hook()
    {
        var cfg = new MemSection();
        var prefs = new PerfPrefs(cfg);
        (string guid, int? rate, bool allow)? pushed = null;
        prefs.OnPluginConfigChanged = (g, r, a) => pushed = (g, r, a);
        prefs.SetPluginRate("x", 60);
        Assert.Equal(("x", (int?)60, false), pushed);
        prefs.SetPluginSelfControl("x", true);
        Assert.Equal(("x", (int?)60, true), pushed);
    }
}
