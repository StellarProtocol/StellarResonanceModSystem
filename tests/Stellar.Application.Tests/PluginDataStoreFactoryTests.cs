using System.IO;
using System.Text;
using Stellar.Infrastructure.Configuration;
using Xunit;

namespace Stellar.Application.Tests;

public class PluginDataStoreFactoryTests
{
    [Fact]
    public void Create_roots_the_store_at_guid_data_subdir()
    {
        var root = Path.Combine(Path.GetTempPath(), "stellar-dsf-" + Path.GetRandomFileName());
        var factory = new PluginDataStoreFactory(root, new NullPluginLog());
        var store = factory.Create("stellar.combatmeter");
        store.Write("replay/r.gz", Encoding.UTF8.GetBytes("x"));
        Assert.True(File.Exists(Path.Combine(root, "stellar.combatmeter.data", "replay", "r.gz")));
    }
}
