using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// The isolated capture's two photo-sized render targets. The bridge copies the queued texture into the isolated
/// runtime's back buffer, draws the effects and copies the result BACK into that texture (bridge isolated.hpp), so a
/// second render of the same texture would stack the effects. The camera is therefore rendered ONCE into the work
/// target and copied to a pristine one; every later render (warm-ups, the real one, a retry) first copies pristine →
/// work. Re-rendering the camera instead would cost a full camera render per frame at up to 4×. Both targets are
/// released at the session's end, in the coroutine's finally and on host loss (FailPending).
/// </summary>
internal sealed partial class UnityFrameGrabber
{
    private RenderTexture? _isolatedWork;
    private RenderTexture? _isolatedPristine;

    /// <summary>The work target, ready to queue: a fresh camera image (also saved as the pristine copy) when
    /// <paramref name="freshCamera"/> or when there is no copy yet, else the pristine copy.</summary>
    private RenderTexture FillIsolatedWork(GrabTarget target, bool freshCamera)
    {
        if (freshCamera || _isolatedWork == null || _isolatedPristine == null)
        {
            ReleaseIsolatedTargets();
            _isolatedWork = NewTarget(target.Size);
            RenderCamera(target, _isolatedWork);
            _isolatedPristine = NewTarget(target.Size);   // same descriptor as the work target: CopyTexture needs it
            Graphics.CopyTexture(_isolatedWork, _isolatedPristine);
            return _isolatedWork;
        }
        Graphics.CopyTexture(_isolatedPristine, _isolatedWork);
        return _isolatedWork;
    }

    private void ReleaseIsolatedTargets()
    {
        DestroyTarget(_isolatedWork);
        DestroyTarget(_isolatedPristine);
        _isolatedWork = _isolatedPristine = null;
    }
}
