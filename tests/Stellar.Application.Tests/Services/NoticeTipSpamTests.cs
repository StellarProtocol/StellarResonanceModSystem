using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// PINNED — notice tips under SPAM.
///
/// <para><b>Origin: owner reports 2026-09-05.</b> (1) Holding a loadout hotkey on the loadout that is
/// already active produced 4-5 frames of 100-185 ms over 3-5 s, even though the framework SKIPPED every
/// switch (<c>switch to 2 skipped: already active</c>, no RPC) — the plugin still toasted "Switched to
/// Beam" each time, and the toast was the only game-engine touch on that path. (2) Clicking "Save current
/// outfit" repeatedly still stuttered (max 428 ms); those toasts carry DISTINCT text, so a text dedupe
/// alone could never have fixed it.</para>
///
/// <para>The cost was <c>Z.UIMgr:OpenView('noticetip_pop')</c> once per tip. On an ALREADY-OPEN view that
/// re-runs the whole activation spine (<c>ui_manager.lua:112-161</c> → <c>ui_base.lua:45-59</c>:
/// SetAsLastSibling + UpdateDepth = a canvas rebuild, then a global <c>UIOpen</c> dispatch), synchronously
/// on the main thread. The fix routes a tip through the game's OWN append path instead — the view drains
/// <c>noticetip_data.pop_msg_data</c> itself (<c>noticetip_pop_view.lua:80-86, :130-133, :210-214</c>) — so
/// only the refresh is paid while a view is live.</para>
/// </summary>
public sealed class NoticeTipSpamTests
{
    private static (NoticeTipService Svc, List<string> Chunks) NewService()
    {
        var chunks = new List<string>();
        var svc = new NoticeTipService(_ => { }, new StubClientState { IsWorldActive = true }, chunks.Add);
        return (svc, chunks);
    }

    private static void Toast(NoticeTipService svc, string content) =>
        svc.Create(NoticeTipType.GreenBar).WithContent(content).Show();

    // ── The show path reaches the game through the game's own queue ───────────────────────────

    /// <summary>PINNED: a tip NEVER re-opens a live noticetip_pop. The chunk asks the UI manager for the
    /// open view and refreshes THAT; the expensive <c>OpenView</c> survives only as the fallback for when
    /// there is no usable view, so no tip can be stranded in the queue.</summary>
    [Fact]
    public void ASecondTipRefreshesTheOpenView_InsteadOfReopeningIt_2026_09_05_toast_spam()
    {
        var (svc, chunks) = NewService();

        Toast(svc, "Switched to Beam");
        svc.Tick();

        var chunk = Assert.Single(chunks);
        Assert.Contains("data:EnqueuePopData(info)", chunk);
        Assert.Contains("Z.UIMgr:GetView('noticetip_pop')", chunk);
        Assert.Contains("v:CallLifeCycleFunc(v.OnRefresh)", chunk);
        // The ONLY OpenView left is the else-branch fallback — never an unconditional call.
        Assert.Contains("else Z.UIMgr:OpenView('noticetip_pop') end", chunk);
        Assert.Equal(1, CountOccurrences(chunk, "Z.UIMgr:OpenView('noticetip_pop')"));
    }

    // ── Volume ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASingleTipShowsOnTheVeryNextTick_NoAddedLatency()
    {
        var (svc, chunks) = NewService();

        Toast(svc, "Saved outfit \"Outfit 1\"");
        svc.Tick();

        Assert.Single(chunks);
    }

    /// <summary>PINNED (owner report 1): five presses of a hotkey that changes nothing produce ONE toast.
    /// The repeat is dropped while its own copy is still on screen — the visible bar already says it.</summary>
    [Fact]
    public void FiveIdenticalTipsInsideTheWindow_ShowOnce_2026_09_05_hotkey_spam()
    {
        var (svc, chunks) = NewService();

        for (var i = 0; i < 5; i++) Toast(svc, "Switched to Beam");
        for (var i = 0; i < 5; i++) svc.Tick();

        Assert.Single(chunks);
    }

    /// <summary>PINNED (owner report 2): five DISTINCT saves keep all five toasts — nothing is lost, and
    /// the last content is the last thing shown — while paying at most ONE OpenView between them, because
    /// every chunk routes through the open view's refresh.</summary>
    [Fact]
    public void FiveDistinctTipsInsideTheWindow_AllShow_AndTheLastContentWins_2026_09_05_save_outfit_spam()
    {
        var (svc, chunks) = NewService();

        for (var i = 1; i <= 5; i++) Toast(svc, $"Saved outfit \"Outfit {i}\"");
        for (var i = 0; i < 5; i++) svc.Tick();

        Assert.Equal(5, chunks.Count);
        Assert.Contains("Outfit 5", chunks[4]);
        foreach (var chunk in chunks)
            Assert.Contains("else Z.UIMgr:OpenView('noticetip_pop') end", chunk);
    }

    /// <summary>PINNED: the drain is bounded to ONE tip per frame. Showing a tip is real main-thread UI
    /// work, so a caller that queues five between two frames must not spend all five in one.</summary>
    [Fact]
    public void TheDrainIsBoundedToOneTipPerTick()
    {
        var (svc, chunks) = NewService();

        for (var i = 1; i <= 5; i++) Toast(svc, $"Saved outfit \"Outfit {i}\"");

        svc.Tick();
        Assert.Single(chunks);

        svc.Tick();
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void NothingIsShownWhileTheWorldIsNotActive()
    {
        var chunks = new List<string>();
        var svc = new NoticeTipService(_ => { }, new StubClientState { IsWorldActive = false }, chunks.Add);

        Toast(svc, "Switched to Beam");
        svc.Tick();

        Assert.Empty(chunks);
    }

    // ── The repeat gate, as a pure decision ───────────────────────────────────────────────────

    [Fact]
    public void TheFirstTipIsNeverGated()
    {
        Assert.Equal(NoticeTipService.ShowDecision.Show,
            NoticeTipService.DecideShow("chunk", lastChunk: null, lastWindowMs: 3200, sinceLastMs: 0));
    }

    [Fact]
    public void AnIdenticalTipInsideItsWindowIsDropped()
    {
        Assert.Equal(NoticeTipService.ShowDecision.DropRepeat,
            NoticeTipService.DecideShow("chunk", lastChunk: "chunk", lastWindowMs: 3200, sinceLastMs: 900));
    }

    /// <summary>PINNED: the gate is a WINDOW, not a mute. Two identical tips five seconds apart are two
    /// separate events to the player and both must show.</summary>
    [Fact]
    public void AnIdenticalTipAfterItsWindowShowsAgain()
    {
        Assert.Equal(NoticeTipService.ShowDecision.Show,
            NoticeTipService.DecideShow("chunk", lastChunk: "chunk", lastWindowMs: 3200, sinceLastMs: 5000));
    }

    /// <summary>PINNED: DIFFERENT content is never dropped, however fast it arrives — that is what makes
    /// the save-outfit spam (distinct text every click) correct rather than silently swallowed.</summary>
    [Fact]
    public void ADifferentTipInsideTheWindowAlwaysShows()
    {
        Assert.Equal(NoticeTipService.ShowDecision.Show,
            NoticeTipService.DecideShow("chunk-b", lastChunk: "chunk-a", lastWindowMs: 3200, sinceLastMs: 5));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
            count++;
        return count;
    }
}
