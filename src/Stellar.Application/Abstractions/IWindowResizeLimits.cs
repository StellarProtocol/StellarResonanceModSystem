namespace Stellar.Application.Abstractions;

/// <summary>Optional renderer capability (mirrors IWindowOrder / IWindowEditHide, keeping IWindowRenderer under the
/// STELLAR0005 member cap): runtime resize limits for one mounted window (<c>IWindowSizeLimits</c>). The renderer
/// rewrites the grip's clamp (which SetRect and the saved-layout restore also clamp against) and clamps the live size
/// in place, top-left fixed. A renderer without it (a test fake) keeps the spec limits.</summary>
internal interface IWindowResizeLimits
{
    /// <summary>Apply the limits to the mounted window behind <paramref name="token"/> (canvas units, already
    /// normalised: max ≥ min).</summary>
    void SetResizeLimits(object? token, float minWidth, float minHeight, float maxWidth, float maxHeight);
}
