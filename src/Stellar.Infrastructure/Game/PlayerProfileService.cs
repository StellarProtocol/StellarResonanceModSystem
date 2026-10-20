using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IPlayerProfile"/> implementation: resolves a player's avatar image URL by char/role id
/// through the game's own <c>Social.GetSocialData</c> RPC (service uuid 901134105, methodId 1).
///
/// <para><b>How the game call is made (all by reflection — this layer has no compile-time game refs):</b>
/// <list type="number">
/// <item>Acquire the proxy through the game's own accessor
///   <c>Zservice.ServiceRegister.GetSocialProxy(IRpcCtrl ctrl, bool attached)</c> fed
///   <c>ZRpcCtrl.Instance</c> (a <c>ZSingleton</c>, resolved via <see cref="StellarInterop.GetSingleton(Type?)"/>).
///   This is the routine the game itself calls, so it fetches-or-creates the registered proxy and
///   attaches its stub — more robust than reaching into <c>proxyDict_</c> (which can be null pre-registration).</item>
/// <item>Build a <c>GetSocialDataRequest{ CharId = charId, Mask = 2 }</c> (Mask bit 2 = the avatar block)
///   and invoke <c>SocialProxy.GetSocialData(request, 0u)</c>, which returns a
///   <c>Cysharp.Threading.Tasks.UniTask&lt;GetSocialDataReply&gt;</c>.</item>
/// <item>The framework cannot <c>await</c> a game <c>UniTask</c> (no compile-time reference), so the task is
///   POLLED each main tick via reflected <c>Status</c>/<c>GetAwaiter()</c>/<c>GetResult()</c> — the exact
///   pattern <c>GameAssetsService</c> proved for the Addressables <c>UniTask</c>.</item>
/// <item>On success the avatar URL is read off
///   <c>reply.Data.AvatarInfo.Profile.Url</c> (a full server-supplied <c>https://…png</c>), or null when
///   the reply faulted, <c>ErrCode != 0</c>, or any link in that chain / the URL is null/empty.</item>
/// </list></para>
///
/// <para><b>Threading:</b> <see cref="RequestAvatarUrl"/> may be called from any thread — it only enqueues.
/// Every game interaction (proxy resolve, request build, <c>GetSocialData</c> invoke, UniTask poll) happens
/// on the main thread in <see cref="Tick"/>, and each callback is marshalled through
/// <see cref="IFrameworkTiming.Post(Action)"/> so plugin UI code always runs on the main thread. Defensive
/// everywhere: a reflection miss or throw fails the one request with a <c>null</c> result and never crosses
/// back into game code.</para>
///
/// <para>Deliberately near-stateless beyond cached reflection metadata and the pending list: concurrent
/// lookups of the same id are NOT coalesced (the plugin caches results). See the sibling
/// <c>PlayerProfileService.Reflection.cs</c> for the reflection/UniTask plumbing.</para>
/// </summary>
internal sealed partial class PlayerProfileService : IPlayerProfile
{
    // Mask bit for the avatar block of SocialData (SocialDataTypeAvatar). GetSocialDataRequest.Mask is int64.
    private const long AvatarMask = 2L;

    private readonly IPluginLog _log;
    private readonly IFramework _framework;

    // Requests land here from any thread; drained + started on the main thread in Tick (no game touch off-thread).
    private readonly ConcurrentQueue<Request> _incoming = new();
    // In-flight UniTask<GetSocialDataReply> values being polled on the main thread. Main-thread-only, no lock.
    private readonly List<InFlight> _inFlight = new();

    public PlayerProfileService(IPluginLog log, IFramework framework)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _framework = framework ?? throw new ArgumentNullException(nameof(framework));
    }

    /// <inheritdoc/>
    public void RequestAvatarUrl(long charId, Action<string?> onResult)
    {
        if (onResult is null) return;                 // nothing to call back
        _incoming.Enqueue(new Request(charId, onResult));
    }

    /// <summary>
    /// Main-thread pump (wired to the framework Update): start any newly-queued requests, then poll the
    /// in-flight UniTasks and fire callbacks for the ones that completed. Never throws.
    /// </summary>
    public void Tick()
    {
        DrainIncoming();
        PollInFlight();
    }

    // Start each queued request on the main thread. A non-positive id or any resolve/build/invoke failure
    // completes that request with null immediately; a successful GetSocialData invoke parks the UniTask.
    private void DrainIncoming()
    {
        while (_incoming.TryDequeue(out var req))
        {
            if (req.CharId <= 0)
            {
                Complete(req.OnResult, null);
                continue;
            }

            object? uniTask = TryStartRequest(req.CharId);
            if (uniTask is null)
            {
                Complete(req.OnResult, null);
                continue;
            }

            _inFlight.Add(new InFlight(uniTask, req.OnResult));
        }
    }

    // Poll every in-flight UniTask once. Pending → leave it; completed (success/fault/cancel) or a poll
    // failure → remove it and complete the callback with the URL (or null). Iterate backwards for in-place removal.
    private void PollInFlight()
    {
        for (int i = _inFlight.Count - 1; i >= 0; i--)
        {
            var f = _inFlight[i];
            bool done = TryPoll(f.UniTask, out string? url);   // on a poll throw: done=true, url=null (fail-safe)
            if (!done) continue;

            _inFlight.RemoveAt(i);
            Complete(f.OnResult, url);
        }
    }

    // Marshal the callback onto the next main tick (exception-isolated) so plugin UI code is always main-thread-safe.
    private void Complete(Action<string?> onResult, string? url)
    {
        _framework.Post(() =>
        {
            try { onResult(url); }
            catch (Exception ex) { _log.Warning($"[PlayerProfile] avatar-url callback threw: {ex.GetType().Name}: {ex.Message}"); }
        });
    }

    private readonly record struct Request(long CharId, Action<string?> OnResult);

    private sealed class InFlight
    {
        public InFlight(object uniTask, Action<string?> onResult)
        {
            UniTask = uniTask;
            OnResult = onResult;
        }

        // Boxed Cysharp UniTask<GetSocialDataReply>; polled via reflection (see Reflection partial).
        public object UniTask { get; }
        public Action<string?> OnResult { get; }
    }
}
