// src/Stellar.Application/Services/AttrReadabilityMemo.cs
using System.Collections.Generic;
using System.Threading;

namespace Stellar.Application.Services;

/// <summary>
/// One attribute-read attempt inside a single sampling pass, as reported by the
/// player-stats probe. <see cref="Read"/> says whether the read produced a live
/// value; <see cref="FullyProbed"/> says whether this pass exhausted every
/// storage type for the id (only such a miss is evidence that the attribute
/// cannot be read at all).
/// </summary>
internal readonly struct AttrReadOutcome
{
    private AttrReadOutcome(int attrId, bool read, bool fullyProbed)
    {
        AttrId = attrId;
        Read = read;
        FullyProbed = fullyProbed;
    }

    /// <summary>The attribute id that was read.</summary>
    public int AttrId { get; }

    /// <summary>True when the read produced a live value from the game's attribute sheet.</summary>
    public bool Read { get; }

    /// <summary>
    /// True when this pass tried every storage type for the id (the first-read probe).
    /// A miss is a latch candidate only when this is true — a miss through an already
    /// memoized storage type means "the sheet went quiet", never "this id does not exist".
    /// </summary>
    public bool FullyProbed { get; }

    /// <summary>A first-read probe that tried every storage type for <paramref name="attrId"/>.</summary>
    public static AttrReadOutcome Probed(int attrId, bool read) => new(attrId, read, true);

    /// <summary>
    /// A read through an already-locked storage-type memo. Counts as readiness evidence
    /// when it hits, but a miss never latches the id.
    /// </summary>
    public static AttrReadOutcome Memoized(int attrId, bool read) => new(attrId, read, false);
}

/// <summary>
/// What <see cref="AttrReadabilityMemo.Record"/> did with one pass. Consumed by the
/// probe for logging only — the memo itself already holds the decision.
/// </summary>
internal readonly struct AttrMemoPassResult
{
    internal AttrMemoPassResult(IReadOnlyList<int> latched, bool skippedNotReady, int attempted, int hits)
    {
        Latched = latched;
        SkippedNotReady = skippedNotReady;
        Attempted = attempted;
        Hits = hits;
    }

    /// <summary>Ids newly committed to the unreadable set by this pass. Empty when nothing latched.</summary>
    public IReadOnlyList<int> Latched { get; }

    /// <summary>True when the whole pass missed and was therefore discarded (attribute sheet not ready).</summary>
    public bool SkippedNotReady { get; }

    /// <summary>Number of read attempts reported in this pass.</summary>
    public int Attempted { get; }

    /// <summary>Number of those attempts that produced a live value.</summary>
    public int Hits { get; }
}

/// <summary>
/// Remembers which attribute ids the game genuinely cannot serve, so the per-tick
/// sampler stops re-probing them (each re-probe of a wrong storage type emits a Unity
/// <c>arr type err</c> line, which at 60&#160;Hz is a log flood).
///
/// <para>
/// The load-bearing rule is <b>an all-miss pass latches nothing until the attribute sheet is
/// known to be populated</b>. The local-player entity exists for several ticks before its
/// sheet is populated, so a sampling pass taken inside the login window misses <i>every</i>
/// id. Treating that as "these attributes do not exist" poisons the memo for the whole
/// process: StatInspector then renders "&#8212;" for every tracked stat until the client is
/// relaunched (owner report 2026-09-21).
/// </para>
///
/// <para>
/// Readiness is therefore an EXPLICIT input (<c>sheetReady</c>), never inferred from the pass.
/// The pass covers only what the installed plugins subscribed to, and a client can subscribe
/// exactly the ids the game does not publish — a CombatMeter-only client subscribes 11760 +
/// 11980 and nothing else, both absent from the wire sheet. Inferring "not ready" from
/// "nothing read" would make every pass on that client all-miss forever: those ids would never
/// latch and the sampler would re-probe them (three reflective invokes each) every tick for the
/// process lifetime — the exact log flood and cost this memo exists to prevent (PR #88 review).
/// With readiness explicit, the login window still latches nothing and genuinely absent ids
/// latch on their first probe after the sheet arrives.
/// </para>
///
/// <para>
/// Pure and BCL-only by design: the decision is unit-pinned in
/// <c>Stellar.Application.Tests</c> while the reflection that feeds it stays in
/// Infrastructure. Reads are lock-free (the set is swapped, never mutated in place) so the
/// per-tick sampler pays nothing; the rare writes take a private lock because
/// <see cref="Forget"/> can arrive from any plugin thread via <c>IPlayerStats.Subscribe</c>.
/// </para>
/// </summary>
internal sealed class AttrReadabilityMemo
{
    private static readonly IReadOnlyList<int> NoIds = new int[0];

    private readonly object _writeLock = new();

    // Treated as immutable once published: writers copy-then-swap, readers take the
    // reference and only ever call Contains on it.
    private HashSet<int> _unreadable = new();

    /// <summary>True when <paramref name="attrId"/> is known unreadable and must be skipped.</summary>
    public bool IsUnreadable(int attrId) => Volatile.Read(ref _unreadable).Contains(attrId);

    /// <summary>
    /// True when <paramref name="pass"/> cannot be judged on its own evidence: it carries at
    /// least one first-read probe that missed, and no live read anywhere. Only such a pass
    /// needs the caller to supply <c>sheetReady</c> to <see cref="Record"/>, so the caller
    /// pays for the readiness probe in that window and never in steady state.
    /// </summary>
    public static bool NeedsReadinessSignal(IReadOnlyList<AttrReadOutcome> pass)
    {
        if (pass is null) return false;

        var candidate = false;
        for (var i = 0; i < pass.Count; i++)
        {
            if (pass[i].Read) return false;
            if (pass[i].FullyProbed) candidate = true;
        }
        return candidate;
    }

    /// <summary>
    /// Commits one sampling pass. Ids whose full probe missed are added to the unreadable set
    /// when the attribute sheet is known to be populated — either because some id in the same
    /// pass produced a live value, or because <paramref name="sheetReady"/> says so explicitly.
    /// A pass that misses everything while the sheet is not ready is discarded untouched.
    /// </summary>
    /// <param name="pass">The read attempts this sampling pass made.</param>
    /// <param name="sheetReady">
    /// The caller's explicit "the game has populated the attribute sheet" signal, read from an
    /// always-present attribute rather than from the pass. It must NOT be derived from the
    /// plugin-subscribed set: a client that only subscribes ids the game never publishes misses
    /// every id on every pass forever, and inferring readiness from the pass alone would leave
    /// those ids re-probed at the tick rate for the process lifetime.
    /// </param>
    public AttrMemoPassResult Record(IReadOnlyList<AttrReadOutcome> pass, bool sheetReady)
    {
        if (pass is null || pass.Count == 0)
        {
            return new AttrMemoPassResult(NoIds, false, 0, 0);
        }

        var hits = 0;
        for (var i = 0; i < pass.Count; i++)
        {
            if (pass[i].Read) hits++;
        }
        if (hits == 0 && !sheetReady)
        {
            // Nothing read at all and the sheet is not populated yet. Latch nothing.
            return new AttrMemoPassResult(NoIds, true, pass.Count, 0);
        }

        List<int>? latched = null;
        lock (_writeLock)
        {
            var current = _unreadable;
            HashSet<int>? next = null;
            for (var i = 0; i < pass.Count; i++)
            {
                var outcome = pass[i];
                if (outcome.Read || !outcome.FullyProbed || current.Contains(outcome.AttrId)) continue;
                next ??= new HashSet<int>(current);
                if (!next.Add(outcome.AttrId)) continue;
                (latched ??= new List<int>()).Add(outcome.AttrId);
            }
            if (next is not null) Volatile.Write(ref _unreadable, next);
        }
        return new AttrMemoPassResult(latched ?? NoIds, false, pass.Count, hits);
    }

    /// <summary>
    /// Drops any "unreadable" verdict for <paramref name="attrId"/> so the next pass probes
    /// it again. Called when a plugin (re-)subscribes the id, which gives the user an
    /// in-game recovery — re-ticking a stat re-probes it instead of needing a relaunch.
    /// Returns true when a verdict was actually dropped.
    /// </summary>
    public bool Forget(int attrId)
    {
        lock (_writeLock)
        {
            var current = _unreadable;
            if (!current.Contains(attrId)) return false;
            var next = new HashSet<int>(current);
            next.Remove(attrId);
            Volatile.Write(ref _unreadable, next);
            return true;
        }
    }

    /// <summary>
    /// Forgets every verdict. Called on a session boundary (logout) so a re-login inside
    /// the same process never inherits a memo seeded during the previous login window.
    /// </summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            if (_unreadable.Count == 0) return;
            Volatile.Write(ref _unreadable, new HashSet<int>());
        }
    }
}
