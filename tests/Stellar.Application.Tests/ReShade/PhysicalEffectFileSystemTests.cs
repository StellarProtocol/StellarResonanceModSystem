using System;
using System.IO;
using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class PhysicalEffectFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stellar-fx-" + Guid.NewGuid().ToString("N"));
    private readonly PhysicalEffectFileSystem _fs = new();

    public PhysicalEffectFileSystemTests() => Directory.CreateDirectory(Path.Combine(_root, "sub"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_file_at_the_size_cap_is_read()
    {
        var path = Path.Combine(_root, "Ok.fx");
        File.WriteAllText(path, new string('a', PhysicalEffectFileSystem.MaxFileBytes));
        Assert.Equal(PhysicalEffectFileSystem.MaxFileBytes, _fs.ReadText(path)!.Length);
    }

    [Fact]
    public void A_file_over_the_size_cap_is_unreadable()
    {
        var path = Path.Combine(_root, "Huge.fx");
        File.WriteAllText(path, new string('a', PhysicalEffectFileSystem.MaxFileBytes + 1));
        Assert.Null(_fs.ReadText(path));
    }

    [Fact]
    public void A_missing_file_reads_as_missing()
    {
        Assert.Null(_fs.ReadText(Path.Combine(_root, "Nope.fx")));
        Assert.Null(_fs.LastWriteTicks(Path.Combine(_root, "Nope.fx")));
    }

    [Fact]
    public void Enumeration_honours_recursion_and_the_limit()
    {
        File.WriteAllText(Path.Combine(_root, "Top.fx"), "t");
        File.WriteAllText(Path.Combine(_root, "sub", "Deep.fx"), "d");
        Assert.Single(_fs.EnumerateFiles(_root, recursive: false, limit: 100));
        Assert.Equal(2, _fs.EnumerateFiles(_root, recursive: true, limit: 100).Count);
        Assert.Single(_fs.EnumerateFiles(_root, recursive: true, limit: 1));
        Assert.Empty(_fs.EnumerateFiles(Path.Combine(_root, "absent"), recursive: true, limit: 100));
    }
}
