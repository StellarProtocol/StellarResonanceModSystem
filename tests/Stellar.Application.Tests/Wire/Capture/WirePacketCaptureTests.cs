using System.Collections.Generic;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Capture;
using Xunit;

namespace Stellar.Application.Tests.Wire.Capture;

public sealed class WirePacketCaptureTests
{
    private sealed class MemSink : ICaptureSink
    {
        public List<string> Lines { get; } = new();
        public bool Truncated => false;
        public void Write(string jsonLine) => Lines.Add(jsonLine);
        public void Dispose() { }
    }

    [Fact]
    public void Record_FilteredOut_NotWritten()
    {
        var sink = new MemSink();
        var cap = new WirePacketCapture(CaptureFilter.Parse("svc:111"), sink);

        cap.RecordForTest("in", FrameBytes.ReturnFrame(1, 5, 0, new byte[] { 0x08, 0x01 }));
        cap.DrainOnceForTest();

        Assert.Empty(sink.Lines);
    }

    [Fact]
    public void Record_All_WritesReturnLineWithDecodedTree()
    {
        var sink = new MemSink();
        var cap = new WirePacketCapture(CaptureFilter.Parse("all"), sink);

        cap.RecordForTest("in", FrameBytes.ReturnFrame(1, 5, 0, new byte[] { 0x08, 0x96, 0x01 }));
        cap.DrainOnceForTest();

        Assert.Single(sink.Lines);
        Assert.Contains("\"type\":\"Return\"", sink.Lines[0]);
        Assert.Contains("\"decoded\"", sink.Lines[0]);
    }

    [Fact]
    public void Record_FrameDownWrappingReturn_EmitsNestedLineAtDepth1()
    {
        var sink = new MemSink();
        var cap = new WirePacketCapture(CaptureFilter.Parse("all"), sink);

        var inner = FrameBytes.ReturnFrame(1, 5, 0, new byte[] { 0x08, 0x01 });
        cap.RecordForTest("in", FrameBytes.FrameDownWrapping(7, inner));
        cap.DrainOnceForTest();

        Assert.Contains(sink.Lines, l => l.Contains("\"depth\":1") && l.Contains("\"type\":\"Return\""));
    }

    [Fact]
    public void TeamPreset_CorrelatedWorldReturn_IsWrittenWithCorrelatedSource()
    {
        var sink = new MemSink();
        var cap = new WirePacketCapture(CaptureFilter.Parse("team"), sink);

        // Outbound World GetTeamInfo Call (svc 103198054, method 311327), callId 42.
        // The team preset filters out World Calls, but NoteCall must still run.
        cap.RecordForTest("out", FrameBytes.CallFrame(103198054, 0, 42, 311327, new byte[] { 0x08, 0x01 }));
        // Inbound anonymous Return on callId 42 (no svc/method on the wire).
        cap.RecordForTest("in", FrameBytes.ReturnFrame(0, 42, 0, new byte[] { 0x08, 0x01 }));
        cap.DrainOnceForTest();

        Assert.Contains(sink.Lines, l => l.Contains("\"svcSource\":\"correlated\"") && l.Contains("\"svc\":103198054"));
    }
}
