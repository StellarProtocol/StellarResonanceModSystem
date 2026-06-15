using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;

namespace Stellar.Infrastructure.Game.Capture;

/// <summary>
/// Off-by-default wire dump. Record() copies+enqueues a frame; a background thread
/// decodes (frame view → schema-less walk → correlate → typed) and writes JSONL.
/// </summary>
internal sealed partial class WirePacketCapture : IDisposable
{
    private readonly CaptureFilter _filter;
    private readonly ICaptureSink _sink;
    private readonly CallReturnCorrelator _correlator = new(capacity: 256);
    private readonly TypedReaderRegistry _typed = new();

    // BoundedChannelFullMode.DropWrite (default for TryWrite) → non-blocking when full.
    private readonly Channel<CaptureFrame> _channel =
        Channel.CreateBounded<CaptureFrame>(new BoundedChannelOptions(4096) { SingleReader = true });

    private readonly ConcurrentDictionary<object, string> _connIds = new();
    private readonly System.Func<object, string> _connIdFactory;
    private int _connSeq = -1;
    private int _dropped;
    private Thread? _drain;
    private volatile bool _running;

    public WirePacketCapture(CaptureFilter filter, ICaptureSink sink)
    {
        _filter = filter;
        _sink = sink;
        _connIdFactory = _ => $"conn#{System.Threading.Interlocked.Increment(ref _connSeq)}";
    }

    public void Start()
    {
        if (!_filter.Enabled || _running) return;
        _running = true;
        _drain = new Thread(DrainLoop) { IsBackground = true, Name = "Stellar-WireCap" };
        _drain.Start();
    }

    public void Record(string direction, byte[] frameBytes, object? connection)
    {
        if (!_filter.Enabled) return;
        var conn = ConnId(connection);
        if (!_channel.Writer.TryWrite(new CaptureFrame(direction, frameBytes, conn)))
            Interlocked.Increment(ref _dropped);
    }

    private string ConnId(object? connection)
    {
        if (connection is null) return "conn#?";
        return _connIds.GetOrAdd(connection, _connIdFactory);
    }

    private void DrainLoop()
    {
        var reader = _channel.Reader;
        while (_running)
        {
            try
            {
                if (!reader.TryRead(out var frame)) { Thread.Sleep(2); continue; }
                ProcessCaptureFrame(frame);
            }
            catch { /* one bad frame must not stop the drain */ }
        }
        while (reader.TryRead(out var frame))
        {
            try { ProcessCaptureFrame(frame); }
            catch { }
        }
    }

    public void Dispose()
    {
        _running = false;
        _drain?.Join(500);
        if (_dropped > 0) _sink.Write($"# dropped {_dropped} frames (channel full)");
        _sink.Dispose();
    }

    internal void RecordForTest(string direction, byte[] frameBytes)
        => Record(direction, frameBytes, connection: null);

    internal void DrainOnceForTest()
    {
        while (_channel.Reader.TryRead(out var frame)) ProcessCaptureFrame(frame);
    }
}
