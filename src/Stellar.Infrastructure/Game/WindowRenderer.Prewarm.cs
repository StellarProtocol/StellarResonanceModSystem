using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

// IWindowHost.PrewarmText → the TMP glyph prewarm queue (TmpGlyphPrewarm). The queue is drained by the window
// ticker's per-rendered-frame Update (wired in EnsureCanvas), so with no canvas nothing is rasterized and queued
// text just waits (bounded) until one exists.
internal sealed partial class WindowRenderer : IWindowTextPrewarm
{
    public void PrewarmText(string text) => TmpGlyphPrewarm.Enqueue(text);
}
