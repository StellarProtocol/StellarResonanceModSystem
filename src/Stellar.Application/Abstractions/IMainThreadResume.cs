using System.Threading.Tasks;
namespace Stellar.Application.Abstractions;

/// <summary>
/// Resumes the caller on the main thread. The narrow slice of <see cref="IFrameGrabber"/> that other
/// Infrastructure services needing the framework's one main-thread resume mechanism depend on, so they
/// share the same ResumeQueue-backed pump as screen capture instead of inventing a second one (see
/// docs/il2cpp-probing-safety.md: work resuming from a pool thread must land back on the main thread
/// before touching anything IL2CPP/Unity-facing, and a plugin's download continuation may do exactly that).
/// </summary>
internal interface IMainThreadResume
{
    /// <summary>
    /// Resumes on the main thread. Infrastructure implements this as a one-frame coroutine backed by a TCS
    /// created without RunContinuationsAsynchronously, so the continuation runs synchronously on the main
    /// thread when the coroutine completes it. The implementation MUST always complete the returned task
    /// (fault it when the coroutine host is gone) — a task that never completes wedges the caller.
    /// </summary>
    Task ResumeOnMainThreadAsync();
}
