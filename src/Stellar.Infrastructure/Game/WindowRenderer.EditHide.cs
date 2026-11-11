using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>Layout-editor "Hide others" for the renderer (<see cref="IWindowEditHide"/>): a per-token flag that
/// ApplyValues honours ABOVE the edit-mode force-show — without it the non-matching overlays stayed drawn (only their
/// outlines vanished; in-game 2026-11-11). Transient: set by WindowService on every apply from its EditHidden flag.</summary>
internal sealed partial class WindowRenderer : IWindowEditHide
{
    public void SetEditHidden(object? token, bool hidden)
    {
        if (token is WindowBuilder.WindowToken t) t.EditHidden = hidden;
    }
}
