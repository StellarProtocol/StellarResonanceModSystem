namespace Stellar.Application.Abstractions;

/// <summary>Renderer capability behind the Host's window PRE-WARM (WindowPrewarm): keep a pre-warmed window ACTIVE but
/// off-screen after its mount, applied like a shown window, until its background work is done — so its first real open
/// is a plain re-show (WindowRenderer.BackgroundWarm.cs).</summary>
internal interface IWindowBackgroundWarm
{
    /// <summary>Start/stop the background warm of window <paramref name="windowId"/>. May be called before the window is
    /// mounted (the mount then lands off-screen). Stopping moves it back to its real place on its next apply / park.</summary>
    void SetBackgroundWarm(string windowId, bool on);

    /// <summary>Its background work is finished: every prebuilt branch built, laid out and parked, and the bubbles settled.</summary>
    bool IsBackgroundWarmDone(string windowId);
}
