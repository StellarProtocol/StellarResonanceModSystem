using System;
using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class ContractTests
{
    [Fact]
    public void CaptureRequest_defaults_ApplyReShade_to_true()
    {
        var request = new CaptureRequest();
        Assert.True(request.ApplyReShade);
    }

    [Fact]
    public void DownloadRequest_IncludePrefixes_defaults_to_null()
    {
        var request = new DownloadRequest(new Uri("https://example.com/file.zip"), "deadbeef", 1024, "shaders", true);
        Assert.Null(request.IncludePrefixes);
    }

    [Fact]
    public void DownloadRequest_has_value_equality()
    {
        var url = new Uri("https://example.com/file.zip");
        var a = new DownloadRequest(url, "deadbeef", 1024, "shaders", true);
        var b = new DownloadRequest(url, "deadbeef", 1024, "shaders", true);
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { Sha256 = "other" });
    }

    [Fact]
    public void DownloadResult_has_value_equality()
    {
        Assert.Equal(new DownloadResult(true, "folder", null), new DownloadResult(true, "folder", null));
        Assert.NotEqual(new DownloadResult(true, "folder", null), new DownloadResult(false, null, "error"));
    }

    [Fact]
    public void ReShadeTechnique_has_value_equality()
    {
        Assert.Equal(new ReShadeTechnique("Bloom", "Bloom.fx", true, false),
            new ReShadeTechnique("Bloom", "Bloom.fx", true, false));
        Assert.NotEqual(new ReShadeTechnique("Bloom", "Bloom.fx", true, false),
            new ReShadeTechnique("Bloom", "Bloom.fx", false, false));
    }

    // ReShade support ships in framework 2.17.0 (IPluginDownloads, IReShade, CaptureRequest.ApplyReShade, CaptureResult.Notes);
    // 2.17.1 is its stable release (same code). The exact pin follows the current version.
    [Fact]
    public void Framework_version_is_2_17_1() => Assert.Equal("2.17.1", FrameworkVersion.Value);

    [Fact]
    public void CaptureResult_Notes_defaults_to_empty_for_ok_and_fail()
    {
        Assert.Empty(CaptureResult.Ok("p", 1, 1).Notes);
        Assert.Empty(CaptureResult.Fail("e").Notes);
    }
}
