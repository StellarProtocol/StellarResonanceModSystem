namespace Stellar.Application.Abstractions;

/// <summary>Outbound port for window draw-order operations. Implemented by WindowRenderer alongside
/// <see cref="IWindowRenderer"/>; obtained via cast in WindowService so IWindowRenderer stays under the
/// member cap enforced by STELLAR0005.</summary>
internal interface IWindowOrder
{
    void BringToFront(object? token);
}
