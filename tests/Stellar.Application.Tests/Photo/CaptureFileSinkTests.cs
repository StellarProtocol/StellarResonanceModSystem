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

    // Photo Studio fw fix round (perf review): the PNG path streams into the file through a callback; the sink keeps
    // its never-overwrite / CreateNew semantics and removes ITS OWN half-written file when the writer throws.
    [Fact]
    public void Streams_through_the_callback_with_the_same_never_overwrite_rule()
    {
        var dir = Path.Combine(Path.GetTempPath(), "photo-sink-" + Guid.NewGuid().ToString("N"));
        var sink = new CaptureFileSink();
        var a = sink.Write(dir, "BPSR_x", ".png", new byte[] { 1 });
        var b = sink.WriteNew(dir, "BPSR_x", ".png", s => s.Write(new byte[] { 7, 8 }));
        Assert.Equal(Path.Combine(dir, "BPSR_x_1.png"), b);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(a));
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(b));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_throwing_writer_leaves_no_partial_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "photo-sink-" + Guid.NewGuid().ToString("N"));
        var sink = new CaptureFileSink();
        Assert.Throws<InvalidOperationException>(() =>
            sink.WriteNew(dir, "BPSR_x", ".png", s => { s.Write(new byte[] { 1, 2, 3 }); throw new InvalidOperationException("encode failed"); }));
        Assert.Empty(Directory.GetFiles(dir));
        Directory.Delete(dir, true);
    }
}
