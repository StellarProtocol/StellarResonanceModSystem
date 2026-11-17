using System;

namespace Stellar.Abstractions.Services;

/// <summary>Which game table a <see cref="IGameText"/> id belongs to. Values are pinned — append only.</summary>
public enum GameTextKind
{
    /// <summary>A <c>BuffTable</c> row id.</summary>
    Buff = 0,
    /// <summary>A <c>SkillTable</c> row id (also the id a combat event names its skill by).</summary>
    Skill = 1,
}

/// <summary>
/// English text for buff and skill rows, shipped with the framework so plugins no longer embed their own copy of
/// the translated BuffTable. The game leaves many internal rows (proc buffs, sub-buffs, damage zones) without a
/// localized name — only a Chinese design label (<c>NameDesign</c>) — and the framework's own
/// <see cref="IGameDataCombat"/> names then read "Buff &lt;id&gt;" / "Skill &lt;id&gt;". This service carries an
/// English translation of those design labels (plus the English name / description / note where the game has them).
///
/// <para><b>Loading.</b> Nothing is read until the first call on this service; the table (≈190 KB compressed,
/// ≈15k rows) is then parsed on a background thread and published in one step. Until <see cref="IsReady"/> is true
/// every lookup returns null and <see cref="ResolveDisplayName"/> returns the game name — so callers must tolerate
/// the not-ready path and re-resolve once on <see cref="Ready"/>. Lookups are O(1), allocation-free and safe from
/// any thread.</para>
/// </summary>
public interface IGameText
{
    /// <summary>True once the table is loaded (also true, with an empty table, when loading failed — no retry).</summary>
    bool IsReady { get; }

    /// <summary>Raised once per handler, on the main thread, when the table becomes ready. Subscribing also starts
    /// the load. A handler added after the table is ready still runs once, on the next framework tick — so
    /// "subscribe, then re-resolve labels in the handler" never misses the refresh. Exceptions are logged and
    /// swallowed per handler.</summary>
    event Action Ready;

    /// <summary>English localized name of a row, or null when the row has no real name (internal rows), the id is
    /// unknown, or the table is not ready yet. Use the game's own name first — this is the English-client text.</summary>
    string? GetName(GameTextKind kind, int id);

    /// <summary>English translation of the row's design label (<c>NameDesign</c>, e.g. buff 2203291 →
    /// "Firmament Fall-child buff"), or null. A designer label, not player-facing wording — show it only when
    /// the row has no real name, and ideally with its id.</summary>
    string? GetDesignName(GameTextKind kind, int id);

    /// <summary>English description of the row; for a buff with no description, its English design note. Null when
    /// neither exists or the table is not ready.</summary>
    string? GetDescription(GameTextKind kind, int id);

    /// <summary>True when <paramref name="name"/> means "no name": null/blank, the game's filler strings for an
    /// unnamed row ("气刃突刺计数" / "Spirit Blade Thrust count" for buffs, "场地标记01" for skills), or the
    /// framework's id fallback "Buff &lt;digits&gt;" / "Skill &lt;digits&gt;". Allocation-free.</summary>
    bool IsPlaceholderName(string? name);

    /// <summary>
    /// The label to show for a row, given the name the game (or <see cref="IGameDataCombat"/>) gave you:
    /// <list type="number">
    ///   <item><paramref name="gameName"/> when it is a real name and the row is not known to be unnamed;</item>
    ///   <item>else the English name (<see cref="GetName"/>);</item>
    ///   <item>else the English design name (<see cref="GetDesignName"/>) — <paramref name="isDesignFallback"/> = true,
    ///         so you can mark it, e.g. append " (&lt;id&gt;)";</item>
    ///   <item>else <paramref name="gameName"/> when non-blank, else "Buff &lt;id&gt;" / "Skill &lt;id&gt;".</item>
    /// </list>
    /// Allocates only on the last fallback. Before <see cref="IsReady"/> this returns step 1 or 4.
    /// </summary>
    string ResolveDisplayName(GameTextKind kind, int id, string? gameName, out bool isDesignFallback);
}
