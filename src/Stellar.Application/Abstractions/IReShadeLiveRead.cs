namespace Stellar.Application.Abstractions;

/// <summary>
/// Optional capability of an <c>IReShade</c> implementation whose state is a polled snapshot: re-read it right now.
/// The capture service calls it just before choosing a photo's active techniques, so a technique switched on a moment
/// before the shutter (for example a depth effect) is in the plan instead of waiting for the next poll.
/// </summary>
internal interface IReShadeLiveRead
{
    /// <summary>Re-reads availability and the technique list immediately (main thread; never throws).</summary>
    void RefreshNow();
}
