using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Stellar.Infrastructure.Configuration;
using Xunit;

namespace Stellar.Application.Tests;

public class PluginDataStoreTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    private PluginDataStore NewStore(out string dataDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "stellar-ds-" + Path.GetRandomFileName());
        _tempRoots.Add(root);
        dataDir = Path.Combine(root, "test.plugin.data");
        return new PluginDataStore(root, "test.plugin", new NullPluginLog());
    }

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* best-effort cleanup; never fail a test on teardown */ }
        }
    }

    [Fact]
    public void Write_then_Read_roundtrips_bytes()
    {
        var store = NewStore(out _);
        var payload = Encoding.UTF8.GetBytes("hello-world");
        store.Write("replay/1-2.gz", payload);
        Assert.Equal(payload, store.Read("replay/1-2.gz"));
    }

    [Fact]
    public void Read_absent_returns_null()
    {
        var store = NewStore(out _);
        Assert.Null(store.Read("replay/nope.gz"));
    }

    [Fact]
    public void Delete_removes_the_file()
    {
        var store = NewStore(out _);
        store.Write("replay/x.gz", new byte[] { 1 });
        store.Delete("replay/x.gz");
        Assert.Null(store.Read("replay/x.gz"));
    }

    [Fact]
    public void List_filters_by_prefix_with_forward_slashes()
    {
        var store = NewStore(out _);
        store.Write("replay/a.gz", new byte[] { 1 });
        store.Write("replay/b.gz", new byte[] { 2 });
        store.Write("other/c.gz", new byte[] { 3 });
        var names = store.List("replay/");
        Assert.Equal(2, names.Count);
        Assert.Contains("replay/a.gz", names);
        Assert.Contains("replay/b.gz", names);
    }

    [Theory]
    [InlineData("../escape.gz")]
    [InlineData("a/../../escape.gz")]
    [InlineData("/etc/passwd")]
    [InlineData("a/b/c.gz")]      // more than one separator
    [InlineData("back\\slash.gz")]
    [InlineData("")]
    [InlineData("foo\0bar.gz")]   // embedded NUL — Path.GetFullPath throws; TryResolve must swallow it
    public void Invalid_names_are_refused(string name)
    {
        var store = NewStore(out _);
        store.Write(name, new byte[] { 9 });          // must not throw, must not write
        Assert.Null(store.Read(name));                // read of invalid name → null
        Assert.Empty(store.List());                   // nothing written anywhere in the data dir
    }
}

// Minimal IPluginLog test double (no BepInEx).
internal sealed class NullPluginLog : Stellar.Abstractions.Services.IPluginLog
{
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
}
