using System;
using System.IO;
using Stellar.Application.Imaging;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class CaptureFileSinkTests
{
    [Fact]
    public void Creates_folder_and_suffixes_on_collision()
    {
        var dir = Path.Combine(Path.GetTempPath(), "photo-sink-" + Guid.NewGuid().ToString("N"));
        var sink = new CaptureFileSink();
        var a = sink.Write(dir, "BPSR_x", ".png", new byte[] { 1 });
        var b = sink.Write(dir, "BPSR_x", ".png", new byte[] { 2 });
        Assert.Equal(Path.Combine(dir, "BPSR_x.png"), a);
        Assert.Equal(Path.Combine(dir, "BPSR_x_1.png"), b);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(b));
        Directory.Delete(dir, true);
    }
}
