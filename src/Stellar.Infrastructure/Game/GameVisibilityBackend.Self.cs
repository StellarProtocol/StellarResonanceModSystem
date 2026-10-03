using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

/// <summary>Self: the game's own photo switches Oneself (1) and SelfPet (14) — CameraFrameCtrl.SetEntityShow, refcounted
/// in ZEntityMgr and shared with the game's camera panel, so we hold each once and show each once (EntityShowPlan).</summary>
internal sealed partial class GameVisibilityBackend
{
    private readonly EntityShowPlan _selfShow = new();

    private void StepSelf(VisibilityLayers requested)
    {
        var want = (requested & VisibilityLayers.Self) != 0;
        var target = want ? EntityShowPlan.SelfSet : Array.Empty<int>();
        var wrote = _selfShow.NeedsWrite(target, HoldCount);
        var ok = !wrote || Invoke(VisibilityLayers.Self, want, () => SetSelfHidden(target));
        _applied = (_applied & ~VisibilityLayers.Self) | (want && ok ? VisibilityLayers.Self : VisibilityLayers.None);
        if (wrote)
        {
            OnLayerSet(VisibilityLayers.Self, want, ok);
            OnSelfHoldsChanged(want);
        }
    }

    private bool SetSelfHidden(int[] target)
    {
        if (EntityShowWriter("Self") is not { } write) return false;
        return _selfShow.Apply(target, write, HoldCount);
    }

    /// <summary>StellarDiagnostics-only: logs the live ETakePhotos hold counts after a Self write (GameVisibilityBackend.Diagnostics.cs).</summary>
    partial void OnSelfHoldsChanged(bool hide);
}
