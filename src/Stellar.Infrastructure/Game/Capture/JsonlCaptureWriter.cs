using System.IO;

namespace Stellar.Infrastructure.Game.Capture;

/// <summary>JSONL file sink with a per-session line cap (no silent caps).</summary>
/// <remarks>
/// <c>AutoFlush=true</c> on purpose: the game is killed (not gracefully unloaded)
/// on close/relaunch, so <see cref="Dispose"/>'s flush often never runs. Flushing
/// per line keeps every completed line durable + the file always ending on a line
/// boundary; a kill can truncate only the single in-flight line (skippable by
/// readers). This runs on the off-thread drain loop and only when capture is
/// enabled, so the extra flushes are not a production concern.
/// </remarks>
internal sealed class JsonlCaptureWriter : ICaptureSink
{
    private readonly StreamWriter _writer;
    private readonly int _maxLines;
    private int _lines;

    public bool Truncated { get; private set; }

    public JsonlCaptureWriter(string path, int maxLines)
    {
        _maxLines = maxLines;
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
    }

    public void Write(string jsonLine)
    {
        if (_lines >= _maxLines)
        {
            if (!Truncated) { Truncated = true; _writer.WriteLine($"# truncated at {_maxLines} lines"); }
            return;
        }
        _writer.WriteLine(jsonLine);
        _lines++;
    }

    public void Dispose() { _writer.Flush(); _writer.Dispose(); }
}
