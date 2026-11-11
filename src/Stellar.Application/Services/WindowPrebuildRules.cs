using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>What <c>WindowSpec.Prebuild</c> may do right now (WindowService.Prebuild.cs).</summary>
internal enum PrebuildStage
{
    /// <summary>Nothing: boot, or the window canvas isn't settled.</summary>
    None,
    /// <summary>Build trees without values (login / character select, or a zone load with the world down).</summary>
    Structure,
    /// <summary>The world is up: fill (one off-screen apply of every value).</summary>
    Fill,
}

/// <summary>One tick's environment for the prebuild scheduler, gathered by the Host.</summary>
/// <param name="Stage">From <see cref="WindowPrebuildRules.StageFor"/>.</param>
/// <param name="InWorld">Phase == World. Its rising edge (a new login into the world) re-queues every fill.</param>
/// <param name="BehindLoading">A loading screen covers the game: a fill's freeze is invisible, no pacing.</param>
/// <param name="InCombat">The local player is in combat: a fill during play waits.</param>
/// <param name="Busy">The framework's own launcher / Settings hub pre-warm is running: start nothing this tick, so two big
/// builds never land in one frame (a running fill continues).</param>
internal readonly record struct PrebuildGate(PrebuildStage Stage, bool InWorld, bool BehindLoading, bool InCombat, bool Busy);

/// <summary>The Unity-free decisions of <c>WindowSpec.Prebuild</c> (pure, unit-tested).</summary>
internal static class WindowPrebuildRules
{
    /// <summary>Seconds between two fills during play (none behind a loading screen): each fill is one hitch of that
    /// window's first-apply size, so several pre-built windows never stack into one long freeze.</summary>
    public const float PlayGapSeconds = 3f;

    /// <summary>A fill that never reports done (canvas dropped, a bubble that re-measures forever) ends after this.</summary>
    public const float MaxFillSeconds = 30f;

    /// <summary>Ticks a fill always lasts, so its mount and first apply land whatever the tick ordering.</summary>
    public const int MinFillTicks = 3;

    /// <summary>Seconds between two samples of a filled window's <c>PrebuildSignature</c>.</summary>
    public const float SignatureEverySeconds = 0.5f;

    /// <summary>The stage for this tick. Structure never pulls a plugin value, so it may run before the world (and
    /// behind any loading screen); a fill runs plugin value lambdas, so it needs the world up (IsWorldActive) — a zone
    /// load (world down) only builds structure. Before the client language is known the structure would bake English
    /// button labels, so the title screen waits for it (character select always has it).</summary>
    public static PrebuildStage StageFor(GamePhase phase, bool worldActive, bool canvasReady, bool languageKnown)
    {
        if (!canvasReady || phase == GamePhase.Startup) return PrebuildStage.None;
        if (phase == GamePhase.World) return worldActive ? PrebuildStage.Fill : PrebuildStage.Structure;
        return languageKnown || phase == GamePhase.CharSelect ? PrebuildStage.Structure : PrebuildStage.None;
    }

    /// <summary>A fill may start: behind a loading screen at once; during play only outside combat and
    /// <see cref="PlayGapSeconds"/> after the previous fill ended.</summary>
    public static bool MayStartFill(bool behindLoading, bool inCombat, float secondsSinceLastFill)
        => behindLoading || (!inCombat && secondsSinceLastFill >= PlayGapSeconds);

    /// <summary>A running fill ends: its renderer reports the background work done (never before
    /// <see cref="MinFillTicks"/>), or it hit <see cref="MaxFillSeconds"/>.</summary>
    public static bool FillEnds(int ticks, float seconds, bool done)
        => ticks >= MinFillTicks && (done || seconds >= MaxFillSeconds);
}
