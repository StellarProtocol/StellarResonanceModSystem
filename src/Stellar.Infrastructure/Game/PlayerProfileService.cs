using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IPlayerProfile"/> implementation: resolves a player's avatar image URL by char id through the
/// framework's already-proven social pipeline — NOT a direct Social proxy call.
///
/// <para><b>Why this shape.</b> The earlier version reflected
/// <c>Zservice.ServiceRegister.GetSocialProxy(IRpcCtrl, bool)</c> with the <c>ZRpcCtrl</c> singleton. That
/// threw every call — the Il2CppInterop <c>ZRpcCtrl</c> wrapper does not declare the <c>IRpcCtrl</c> interface
/// and a C# interface can't be <c>.Cast&lt;&gt;</c>'d onto — so no request ever issued and every avatar came
/// back null. This version reuses the pipeline that already works for arbitrary players:</para>
///
/// <list type="number">
/// <item><see cref="ISocialRefreshRequester.RequestSelfSocialData(long)"/> originates the game's own
///   <c>socialVM.AsyncGetSocialData(0, charId, …)</c> via Lua (the "Self" in the name is a misnomer — it takes
///   any charId, as <c>CombatService</c> does for inspected players). <b>Main-thread only</b> (Lua VM).</item>
/// <item>The reply is wire-tapped by <c>PandaSocialDataProbe</c> and pushed into <see cref="SocialDataCache"/>
///   as a <c>SocialSnapshot</c> keyed by charId. We then read <c>SocialSnapshot.ProfileUrl</c> (the full
///   server-supplied <c>https://…png</c>; <c>""</c> when the player has no custom photo).</item>
/// </list>
///
/// <para><b>Threading.</b> <see cref="RequestAvatarUrl"/> may be called from any thread — it only enqueues.
/// Everything else (the Lua originate + the cache read + the callback) runs on the main thread in
/// <see cref="Tick"/>, which the framework wires to Update, so the <see cref="IPlayerProfile"/> main-thread
/// callback contract holds without any extra marshalling. Every callback is exception-isolated; a throw never
/// crosses back into the caller.</para>
/// </summary>
internal sealed class PlayerProfileService : IPlayerProfile
{
    // A reply usually lands within a tick or two. If it hasn't after this long, re-originate once (the first
    // request can race login / an un-synced social VM); give up entirely after the timeout.
    private const double ReRequestAfterSeconds = 4.0;
    private const double TimeoutSeconds = 10.0;

    private readonly IPluginLog _log;
    private readonly ISocialRefreshRequester _socialRefresh;
    private readonly SocialDataCache _social;

    // Requests land here from any thread; drained + originated on the main thread in Tick (no Lua touch off-thread).
    private readonly ConcurrentQueue<Request> _incoming = new();
    // Requests awaiting a SocialDataCache snapshot. Main-thread-only, no lock.
    private readonly List<Pending> _pending = new();
    private bool _firstResolveLogged;

    public PlayerProfileService(IPluginLog log, ISocialRefreshRequester socialRefresh, SocialDataCache social)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _socialRefresh = socialRefresh ?? throw new ArgumentNullException(nameof(socialRefresh));
        _social = social ?? throw new ArgumentNullException(nameof(social));
    }

    /// <inheritdoc/>
    public void RequestAvatarUrl(long charId, Action<string?> onResult)
    {
        if (onResult is null) return;                 // nothing to call back
        _incoming.Enqueue(new Request(charId, onResult));
    }

    /// <summary>
    /// Main-thread pump (wired to the framework Update): originate any newly-queued requests, then poll the
    /// pending list against the social cache and fire callbacks for the ones that have a snapshot (or timed out).
    /// Never throws.
    /// </summary>
    public void Tick()
    {
        DrainIncoming();
        PollPending();
    }

    // Start each queued request on the main thread. A non-positive id completes with null immediately; an id
    // already in the cache completes immediately; otherwise originate the social-data request and park it.
    private void DrainIncoming()
    {
        while (_incoming.TryDequeue(out var req))
        {
            if (req.CharId <= 0)
            {
                Complete(req.OnResult, null);
                continue;
            }

            var snapshot = _social.GetByCharId(req.CharId);
            if (snapshot is not null)
            {
                Complete(req.OnResult, NormalizeUrl(snapshot.ProfileUrl));   // already cached from an earlier fetch
                continue;
            }

            // Not cached yet — originate the game's own request (we're on the main thread) and wait for the tap.
            _socialRefresh.RequestSelfSocialData(req.CharId);
            _pending.Add(new Pending(req.CharId, req.OnResult, DateTime.UtcNow));
        }
    }

    // Poll every pending request against the cache. A snapshot → complete with its URL and remove (every pending
    // entry for the same charId resolves on this same pass → coalesced). Past the timeout → complete with null.
    // Past the re-request mark with no reply yet → re-originate once. Iterate backwards for in-place removal.
    private void PollPending()
    {
        var now = DateTime.UtcNow;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];

            var snapshot = _social.GetByCharId(p.CharId);
            if (snapshot is not null)
            {
                _pending.RemoveAt(i);
                Complete(p.OnResult, NormalizeUrl(snapshot.ProfileUrl));
                continue;
            }

            double elapsed = (now - p.StartUtc).TotalSeconds;
            if (elapsed > TimeoutSeconds)
            {
                _pending.RemoveAt(i);
                Complete(p.OnResult, null);   // the reply never arrived
                continue;
            }

            if (elapsed > ReRequestAfterSeconds && !p.ReRequested)
            {
                p.ReRequested = true;
                _socialRefresh.RequestSelfSocialData(p.CharId);
            }
        }
    }

    // Invoke the callback directly: Tick() runs on the main (Unity) thread, which is the IPlayerProfile
    // contract. Exception-isolated so a throwing plugin callback can't break the pump or cross back out.
    private void Complete(Action<string?> onResult, string? url)
    {
        if (!_firstResolveLogged && url is not null)
        {
            _firstResolveLogged = true;
            _log.Info("[PlayerProfile] first avatar URL resolved via SocialDataCache");
        }

        try { onResult(url); }
        catch (Exception ex) { _log.Warning($"[PlayerProfile] avatar-url callback threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    // "" (player has no custom photo) or null → null; otherwise the full server-supplied https URL.
    private static string? NormalizeUrl(string? url) => string.IsNullOrEmpty(url) ? null : url;

    private readonly record struct Request(long CharId, Action<string?> OnResult);

    private sealed class Pending
    {
        public Pending(long charId, Action<string?> onResult, DateTime startUtc)
        {
            CharId = charId;
            OnResult = onResult;
            StartUtc = startUtc;
        }

        public long CharId { get; }
        public Action<string?> OnResult { get; }
        public DateTime StartUtc { get; }
        public bool ReRequested { get; set; }
    }
}
