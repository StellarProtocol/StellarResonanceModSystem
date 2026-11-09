using System.Linq;
using System.Text.Json.Nodes;
using Stellar.Application.Services;
using Stellar.Application.Tests.Config;
using Stellar.Infrastructure.UI.SettingsPanels;
using Xunit;

namespace Stellar.Application.Tests.Services;

// Hidden developer mode (2.30.0): the persisted flag, the 7-click version unlock, and the Settings rail's tab list.
public sealed class DeveloperModeTests
{
    private const string Guid = "stellar.framework";

    private static (StubConfigStore store, PluginConfigService cfg) NewConfig(JsonObject? initial = null)
    {
        var store = new StubConfigStore();
        if (initial != null) store.Files[Guid] = initial;
        return (store, new PluginConfigService(store, Guid));
    }

    [Fact]
    public void Flag_DefaultsOff_WhenTheConfigHasNoKey()
    {
        var (_, cfg) = NewConfig();
        Assert.False(new DeveloperModePrefs(cfg.GetSection(DeveloperModePrefs.SectionName)).Enabled);
    }

    [Fact]
    public void Flag_ReadsAHandEditedConfigFile()
    {
        var (_, cfg) = NewConfig(new JsonObject { ["developer"] = new JsonObject { ["developerMode"] = true } });
        Assert.True(new DeveloperModePrefs(cfg.GetSection("developer")).Enabled);
    }

    [Fact]
    public void Flag_PersistsUnderDeveloperDotDeveloperMode_AndSurvivesAReload()
    {
        var (store, cfg) = NewConfig();
        var prefs = new DeveloperModePrefs(cfg.GetSection(DeveloperModePrefs.SectionName));
        var raised = new System.Collections.Generic.List<bool>();
        prefs.Changed += raised.Add;

        prefs.Enabled = true;

        Assert.Equal(new[] { true }, raised);
        var saved = store.Files[Guid]!["developer"]!["developerMode"]!.GetValue<bool>();
        Assert.True(saved);
        // A fresh boot over the saved file sees it on.
        var reboot = new PluginConfigService(store, Guid);
        Assert.True(new DeveloperModePrefs(reboot.GetSection("developer")).Enabled);
    }

    [Fact]
    public void Flag_SettingTheSameValue_DoesNotWriteOrRaise()
    {
        var (store, cfg) = NewConfig();
        var prefs = new DeveloperModePrefs(cfg.GetSection("developer"));
        var raised = 0;
        prefs.Changed += _ => raised++;

        prefs.Enabled = false;

        Assert.Equal(0, raised);
        Assert.Empty(store.SaveCalls);
    }

    [Fact]
    public void Reload_PicksUpAnExternalEdit_AndRaisesOnlyOnAChange()
    {
        var (store, cfg) = NewConfig();
        var prefs = new DeveloperModePrefs(cfg.GetSection("developer"));
        cfg.SectionChanged += name => { if (name == DeveloperModePrefs.SectionName) prefs.Reload(); };
        var raised = new System.Collections.Generic.List<bool>();
        prefs.Changed += raised.Add;

        store.RaiseExternalChange(Guid, new JsonObject { ["developer"] = new JsonObject { ["developerMode"] = true } });
        prefs.Reload();   // a same-value reload is a no-op

        Assert.True(prefs.Enabled);
        Assert.Equal(new[] { true }, raised);
    }

    [Fact]
    public void Unlock_SevenQuickClicks_CountDownFromTheFourth()
    {
        var c = new DeveloperUnlockCounter();
        var left = Enumerable.Range(0, 7).Select(i => c.Click(i * 0.4)).ToArray();
        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1, 0 }, left);
        // The 4th click is the first that shows a countdown (3 more).
        Assert.Equal(3, DeveloperUnlockCounter.ClicksNeeded - DeveloperUnlockCounter.CountdownFrom);
    }

    [Fact]
    public void Unlock_APauseLongerThanTheGap_StartsOver()
    {
        var c = new DeveloperUnlockCounter();
        for (var i = 0; i < 5; i++) c.Click(i * 0.5);
        var t = 4 * 0.5 + DeveloperUnlockCounter.MaxGapSeconds + 0.1;
        Assert.Equal(6, c.Click(t));   // counted as the 1st click again
    }

    [Fact]
    public void Unlock_ResetsAfterCompleting()
    {
        var c = new DeveloperUnlockCounter();
        for (var i = 0; i < 7; i++) c.Click(i * 0.2);
        Assert.Equal(6, c.Click(1.6));
    }

    [Fact]
    public void Rail_WithoutDeveloperMode_HasNoDeveloperTab_AndEndsOnAbout()
    {
        var keys = SettingsHub.VisibleTabKeys(developerMode: false);
        Assert.DoesNotContain("developer", keys);
        Assert.Equal("about", keys[^1]);
        Assert.Equal(SettingsHub.Tabs.Length - 1, keys.Count);
    }

    [Fact]
    public void Rail_WithDeveloperMode_ShowsDeveloperLast_WithoutShiftingOtherIndexes()
    {
        var keys = SettingsHub.VisibleTabKeys(developerMode: true);
        Assert.Equal("developer", keys[^1]);
        Assert.Equal("about", keys[^2]);
        Assert.Equal(SettingsHub.Tabs.Select(t => t.Key), keys);
    }

    [Fact]
    public void Rail_VersionLine_GetsDevSuffixOnlyInDeveloperMode()
    {
        Assert.Equal("Stellar 2.30.0", SettingsHub.RailVersion("2.30.0", false));
        Assert.Equal("Stellar 2.30.0 · dev", SettingsHub.RailVersion("2.30.0", true));
    }
}
