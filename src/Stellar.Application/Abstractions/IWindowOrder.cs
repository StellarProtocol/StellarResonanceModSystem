namespace Stellar.Application.Abstractions;

/// <summary>Outbound port for window draw-order operations. Implemented by WindowRenderer alongside
/// <see cref="IWindowRenderer"/>; obtained via cast in WindowService so IWindowRenderer stays under the
/// member cap enforced by STELLAR0005. The order rule itself is <see cref="Services.WindowStacking"/>.</summary>
internal interface IWindowOrder
{
    /// <summary>Activate + (HUD only) lift into the normal layer until the window is next hidden.</summary>
    void BringToFront(object? token);

    /// <summary>"As if just opened": the newest activation, so the window goes on top of its layer.</summary>
    void Activate(object? token);

    /// <summary>Move the window into (true) or out of (false) the Topmost layer.</summary>
    void SetTopmost(object? token, bool topmost);
}
