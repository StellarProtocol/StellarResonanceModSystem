using System.IO;
using System.Linq;
using Stellar.Infrastructure.Game.Capture;
using Xunit;

namespace Stellar.Application.Tests.Wire.Capture;

public sealed class JsonlCaptureWriterTests
{
    [Fact]
    public void Write_EmitsOneJsonLinePerRecord_WithCoreFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wirecap-test-{System.Guid.NewGuid():N}.jsonl");
        try
        {
            using (var w = new JsonlCaptureWriter(path, maxLines: 100))
            {
                w.Write("{\"dir\":\"in\",\"type\":\"Return\",\"callId\":99}");
                w.Write("{\"dir\":\"out\",\"type\":\"Call\"}");
            }
            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"callId\":99", lines[0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Write_FlushesEachLine_DurableBeforeDispose()
    {
        // The game is killed (not gracefully Disposed) on close/relaunch, which used
        // to truncate the buffered tail. With per-line flush, completed lines are on
        // disk immediately — readable here WITHOUT disposing the writer (FileShare.Read).
        var path = Path.Combine(Path.GetTempPath(), $"wirecap-test-{System.Guid.NewGuid():N}.jsonl");
        var w = new JsonlCaptureWriter(path, maxLines: 100);
        try
        {
            w.Write("{\"n\":1}");
            w.Write("{\"n\":2}");

            // Read while the writer is still open (and NOT disposed).
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var content = sr.ReadToEnd();
            var lines = content.Split('\n').Where(l => l.StartsWith("{")).ToArray();

            Assert.Equal(2, lines.Length);
            Assert.Contains("\"n\":1", lines[0]);
            Assert.Contains("\"n\":2", lines[1]);
            Assert.EndsWith("\n", content);   // file ends on a line boundary, not mid-line
        }
        finally
        {
            w.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Write_PastMaxLines_StopsAndLogsTruncationOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wirecap-test-{System.Guid.NewGuid():N}.jsonl");
        try
        {
            using (var w = new JsonlCaptureWriter(path, maxLines: 2))
            {
                w.Write("{\"n\":1}"); w.Write("{\"n\":2}");
                w.Write("{\"n\":3}");
                Assert.True(w.Truncated);
            }
            var lines = File.ReadAllLines(path).Where(l => l.StartsWith("{")).ToArray();
            Assert.Equal(2, lines.Length);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
