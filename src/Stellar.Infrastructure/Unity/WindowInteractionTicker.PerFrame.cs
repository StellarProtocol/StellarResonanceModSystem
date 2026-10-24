namespace Stellar.Infrastructure.Unity;

// Renderer-owned work that must run once per RENDERED frame (this Update), not at the framework tick rate — the
// TMP glyph prewarm spreads its rasterization over frames, so it needs the frame cadence. A delegate rather than a
// direct call so this ticker stays free of TMP types (the renderer wires it: WindowRenderer.EnsureCanvas).
public sealed partial class WindowInteractionTicker
{
    internal System.Action? PerFrame;
    private int _perFrameThrowLogged;

    private void TickPerFrame()
    {
        if (PerFrame == null) return;
        try { PerFrame(); }
        catch { if (_perFrameThrowLogged++ == 0) UnityEngine.Debug.LogWarning("[Window] per-frame hook threw (logged once)"); }
    }
}
