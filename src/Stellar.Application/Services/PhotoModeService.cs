using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

internal sealed class PhotoModeService : IPhotoModeState
{
    public PhotoModeService(IPhotoModeProbe probe)
    {
        probe.KindChanged += OnKind;
        probe.CutsceneChanged += OnCutscene;
    }

    public bool InCutscene { get; private set; }
    public event Action<bool>? CutsceneChanged;

    public bool IsActive => Kind != PhotoModeKind.None;
    public PhotoModeKind Kind { get; private set; }
    public event Action<PhotoModeKind>? Entered;
    public event Action? Exited;

    private void OnKind(PhotoModeKind next)
    {
        if (next == Kind) return;
        if (Kind != PhotoModeKind.None) { Kind = PhotoModeKind.None; Exited?.Invoke(); }
        if (next == PhotoModeKind.None) return;
        Kind = next;
        Entered?.Invoke(next);
    }

    private void OnCutscene(bool on)
    {
        if (on == InCutscene) return;
        InCutscene = on;
        CutsceneChanged?.Invoke(on);
    }
}
