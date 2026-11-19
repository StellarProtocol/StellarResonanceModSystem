using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// <see cref="IGameText"/> over the embedded English game-text table. Shared by every plugin (one copy in memory
/// instead of an 8.6 MB JSON embedded per plugin).
///
/// Loading follows Plugin-Performance-Rules: nothing at boot or world entry; the first call starts ONE background
/// parse (managed data only — never IL2CPP/Unity) that builds the whole table privately and publishes it with one
/// volatile write. Readers see either null (not ready → game-name path) or the complete table. The
/// <see cref="Ready"/> event is marshalled to the main thread through the framework's post queue.
/// </summary>
internal sealed class GameTextService : IGameText
{
    // Filler strings the extracted tables carry for a row with no localized text (see IsPlaceholderName).
    private const string BuffFillerZh = "气刃突刺计数";
    private const string BuffFillerEn = "Spirit Blade Thrust count";
    private const string SkillFillerZh = "场地标记01";

    private readonly IPluginLog _log;
    private readonly Action<Action> _postToMainThread;
    private readonly Func<GameTextTable> _load;
    private readonly Func<int, string?>? _buffGameName;   // the game's own buff name (GetEffectCategory's first signal)
    // Per-id category, filled only once the table is ready (a guess made without the design label would pin Default).
    private readonly ConcurrentDictionary<int, EffectCategory> _categories = new();
    private volatile GameTextTable? _table;   // null until the background parse publishes
    private int _started;                     // 0 → 1 once the parse is queued
    private readonly object _gate = new();
    private Action? _ready;
    private bool _raised;                     // guarded by _gate

    /// <param name="log">Framework log (load result line).</param>
    /// <param name="postToMainThread">Main-thread marshal (the framework's <c>Post</c>), used to raise <see cref="Ready"/>.</param>
    /// <param name="load">Table source; null = the embedded table. Tests pass their own.</param>
    /// <param name="buffGameName">The game's own name for a buff id (<c>IGameDataCombat.GetBuff(id)?.Name</c>), read
    /// by <see cref="GetEffectCategory"/>; null = design label only.</param>
    public GameTextService(IPluginLog log, Action<Action> postToMainThread, Func<GameTextTable>? load = null,
        Func<int, string?>? buffGameName = null)
    {
        _log = log;
        _postToMainThread = postToMainThread;
        _load = load ?? GameTextTable.LoadEmbedded;
        _buffGameName = buffGameName;
    }

    public bool IsReady
    {
        get
        {
            if (_table != null) return true;
            EnsureLoading();
            return false;
        }
    }

    public event Action Ready
    {
        add
        {
            bool late;
            lock (_gate) { late = _raised; if (!late) _ready += value; }
            // Subscribed after the one-shot raise (an IsReady check raced the publish): deliver it anyway.
            if (late) _postToMainThread(() => Invoke(value));
            else EnsureLoading();
        }
        remove { lock (_gate) _ready -= value; }
    }

    public string? GetName(GameTextKind kind, int id) => Row(kind, id)?.Name;

    public string? GetDesignName(GameTextKind kind, int id) => Row(kind, id)?.Design;

    public string? GetDescription(GameTextKind kind, int id) => Row(kind, id)?.Description;

    public bool IsPlaceholderName(string? name) => IsPlaceholder(name);

    public string ResolveDisplayName(GameTextKind kind, int id, string? gameName, out bool isDesignFallback)
    {
        isDesignFallback = false;
        var row = Row(kind, id);
        bool gameNamed = !IsPlaceholder(gameName);
        // A row the English table knows as UNNAMED overrides a non-placeholder game name too: that name is then the
        // framework's curated stand-in for an internal row, and the per-row design name is more specific.
        if (gameNamed && (row == null || row.Name != null)) return gameName!;
        if (row?.Name != null) return row.Name;
        if (row?.Design != null) { isDesignFallback = true; return row.Design; }
        if (!string.IsNullOrWhiteSpace(gameName)) return gameName!;
        return (kind == GameTextKind.Skill ? "Skill " : "Buff ") + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public EffectCategory GetEffectCategory(int buffId)
    {
        if (_categories.TryGetValue(buffId, out var cat)) return cat;
        var t = _table;
        if (t == null) { EnsureLoading(); return EffectCategory.Default; }
        // Signal = the game name when it is a real one (a Chinese client's fallback is the raw Chinese NameDesign; the
        // filler strings are noise) + the English design label. One classification per id, then cached.
        string? name = _buffGameName?.Invoke(buffId);
        if (IsPlaceholder(name)) name = null;
        cat = EffectCategoryClassifier.Classify(name, t.Get(GameTextKind.Buff, buffId)?.Design);
        _categories[buffId] = cat;
        return cat;
    }

    /// <summary>Static form of <see cref="IsPlaceholderName"/> (allocation-free).</summary>
    internal static bool IsPlaceholder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name == BuffFillerZh || name == BuffFillerEn || name == SkillFillerZh) return true;
        return IsIdFallback(name, "Buff ") || IsIdFallback(name, "Skill ");
    }

    // "Buff 123" / "Skill 123" — exactly the prefix followed by one or more ASCII digits.
    private static bool IsIdFallback(string name, string prefix)
    {
        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        for (int i = prefix.Length; i < name.Length; i++)
            if (name[i] < '0' || name[i] > '9') return false;
        return true;
    }

    private GameTextRow? Row(GameTextKind kind, int id)
    {
        var t = _table;
        if (t != null) return t.Get(kind, id);
        EnsureLoading();
        return null;
    }

    private void EnsureLoading()
    {
        if (Volatile.Read(ref _started) != 0 || Interlocked.Exchange(ref _started, 1) != 0) return;
        Task.Run(LoadAndPublish);
    }

    /// <summary>Parses the table, publishes it, then raises <see cref="Ready"/> on the main thread. Runs on a
    /// background task in production; tests call it directly. A failed load publishes an empty table (no retry).</summary>
    internal void LoadAndPublish()
    {
        Volatile.Write(ref _started, 1);
        var sw = Stopwatch.StartNew();
        GameTextTable table;
        string msg;
        bool ok = true;
        try
        {
            table = _load();
            msg = $"[GameText] English game text loaded: {table.BuffCount} buffs, {table.SkillCount} skills in {sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            table = GameTextTable.Empty;
            ok = false;
            msg = $"[GameText] English game text failed to load ({ex.GetType().Name}: {ex.Message}); design names unavailable";
        }
        _table = table;                                      // the one volatile publish
        _postToMainThread(() =>
        {
            if (ok) _log.Info(msg); else _log.Warning(msg);
            RaiseReady();
        });
    }

    private void RaiseReady()
    {
        Action? handlers;
        lock (_gate) { handlers = _ready; _ready = null; _raised = true; }
        if (handlers == null) return;
        foreach (Action h in handlers.GetInvocationList()) Invoke(h);
    }

    // One handler, isolated: a throwing plugin handler must not starve the others.
    private void Invoke(Action handler)
    {
        try { handler(); }
        catch (Exception ex) { _log.Warning($"[GameText] Ready handler threw: {ex.Message}"); }
    }
}
