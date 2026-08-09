using Stellar.Abstractions.Plugins;
using Stellar.Application.Services;
using Stellar.Application.Tests.Theme;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// PluginHost can only seed <c>PluginInfo.DisplayName</c> from the assembly short name
/// (IStellarPlugin.Name needs a constructed instance). These pin the registry's adoption
/// of the plugin's own declared name on enable.
/// </summary>
public sealed class PluginRegistryDisplayNameTests
{
    // The registry only forwards IPluginServices to the factory delegate; every factory
    // here ignores it, so null! is safe and keeps the test off a 40-member stub.
    private static PluginRegistry NewRegistry(out StubLog log)
    {
        log = new StubLog();
        return new PluginRegistry(new InMemoryConfigSection(), log, services: null!);
    }

    private sealed class FakePlugin : IStellarPlugin
    {
        public FakePlugin(string name) => Name = name;
        public string Name { get; }
        public void Dispose() { }
    }

    [Fact]
    public void Enable_AdoptsInstanceDeclaredName_OverRegisteredDisplayName()
    {
        var registry = NewRegistry(out _);

        registry.Register("stellarmahiruutilityplugin", "StellarMahiruUtilityPlugin", "1.0.0",
            _ => new FakePlugin("Mahiru Utility"));

        var info = Assert.Single(registry.List());
        Assert.Equal("Mahiru Utility", info.DisplayName);
        Assert.Equal("stellarmahiruutilityplugin", info.Id);   // identity is untouched
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Enable_BlankInstanceName_KeepsRegisteredDisplayName(string blank)
    {
        var registry = NewRegistry(out _);

        registry.Register("p", "StellarFallbackPlugin", "1.0.0", _ => new FakePlugin(blank));

        Assert.Equal("StellarFallbackPlugin", Assert.Single(registry.List()).DisplayName);
    }

    [Fact]
    public void Enable_NonPluginInstance_KeepsRegisteredDisplayName()
    {
        // The factory is typed Func<IPluginServices, object> — a non-IStellarPlugin
        // return must not blow up or blank the row.
        var registry = NewRegistry(out _);

        registry.Register("p", "StellarOddPlugin", "1.0.0", _ => new object());

        Assert.Equal("StellarOddPlugin", Assert.Single(registry.List()).DisplayName);
    }

    [Fact]
    public void SoftCycleReEnable_ReadoptsCurrentInstanceName()
    {
        var registry = NewRegistry(out _);
        registry.Register("p", "StellarThingPlugin", "1.0.0", _ => new FakePlugin("The Thing"));

        registry.SetEnabled("p", false);
        Assert.False(Assert.Single(registry.List()).IsEnabled);

        registry.SetEnabled("p", true);
        var info = Assert.Single(registry.List());
        Assert.True(info.IsEnabled);
        Assert.Equal("The Thing", info.DisplayName);
    }
}
