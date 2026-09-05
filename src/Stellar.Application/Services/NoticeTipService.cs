using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

public sealed class NoticeTipService : INoticeTips
{
    private readonly Action<string> _log;
    private readonly IClientState _clientState;
    private readonly Action<string>? _runChunk;
    private readonly ConcurrentQueue<(string Chunk, int WindowMs)> _pending = new();
    private object? _luaState;
    private MethodInfo? _luaDoString;

    // Repeat gate state — main thread only (written and read inside Tick).
    private string? _lastChunk;
    private int     _lastWindowMs;
    private long    _lastShowStamp;

    private const int  MinWindowMs = 250;
    private const int  MaxWindowMs = 10_000;
    private const long SlowShowMs  = 33;   // ~2 frames at 60 Hz

    public NoticeTipService(Action<string> log, IClientState clientState) : this(log, clientState, null) { }

    // Test seam (internal — plugins never see it): the production path binds Lua's DoString by
    // reflection against the live game (EnsureLuaState), so Application-layer unit tests inject the
    // invoker instead. Nothing else differs; the queue, the gate and the timing all run as shipped.
    internal NoticeTipService(Action<string> log, IClientState clientState, Action<string>? runChunk)
    {
        _log = log;
        _clientState = clientState;
        _runChunk = runChunk;
    }

    public INoticeTipBuilder Create(NoticeTipType type) => new LuaNoticeTipBuilder(this, type);

    /// <summary>Outcome of the repeat gate on the show path — see <see cref="DecideShow"/>.</summary>
    internal enum ShowDecision { Show, DropRepeat }

    /// <summary>Pure decision for the repeat gate (pinned by <c>NoticeTipSpamTests</c>; origin = owner
    /// report 2026-09-05, hotkey spam re-toasting "Switched to Beam" while the previous copy was still on
    /// screen). A chunk BYTE-IDENTICAL to the one still inside its own display window adds nothing the
    /// player cannot already read, so it is dropped — mirroring the game's own repeat filter
    /// (<c>noticetip_data.lua:8-22 checkConfigRepeat</c>, which never fires for us because our tips carry
    /// <c>Id=0</c> and so have no MessageTable row). The FIRST tip is never gated, and a tip with
    /// DIFFERENT content always shows.</summary>
    internal static ShowDecision DecideShow(string chunk, string? lastChunk, int lastWindowMs, long sinceLastMs)
        => lastChunk is not null && sinceLastMs < lastWindowMs && string.Equals(chunk, lastChunk, StringComparison.Ordinal)
            ? ShowDecision.DropRepeat
            : ShowDecision.Show;

    // Called from the Unity main thread each frame — runs game Lua (DoString), so it must NOT touch the
    // game during the world-connect handshake. Self-gates on IsWorldActive.
    //
    // ONE tip per tick. Showing a tip is real main-thread UI work, so the drain is bounded to a single
    // chunk per frame rather than however many a spamming caller queued between two frames (owner report
    // 2026-09-05). A lone tip still shows on the very next tick — no added latency on a single click.
    [WorldGated]
    public void Tick()
    {
        if (!_clientState.IsWorldActive) return;
        if (_pending.IsEmpty) return;
        if (!EnsureInvoker()) return;
        if (!_pending.TryDequeue(out var tip)) return;
        ShowOne(tip);
    }

    private void ShowOne((string Chunk, int WindowMs) tip)
    {
        var since = _lastChunk is null ? long.MaxValue : ElapsedMs(_lastShowStamp);
        if (DecideShow(tip.Chunk, _lastChunk, _lastWindowMs, since) == ShowDecision.DropRepeat) return;

        var started = Stopwatch.GetTimestamp();
        try { Invoke(tip.Chunk); }
        catch (Exception ex) { _log($"[NoticeTips] Lua error: {ex.Message}"); }
        var ms = ElapsedMs(started);

        _lastChunk     = tip.Chunk;
        _lastWindowMs  = tip.WindowMs;
        _lastShowStamp = Stopwatch.GetTimestamp();

        // ALWAYS ON, and deliberately NOT behind StellarDiagnostics in a .Diagnostics.cs partial: the line
        // is SELF-LIMITING (nothing at all is logged under the threshold, so normal play emits zero
        // lines), and it has to be readable in the owner's very next log WITHOUT a diagnostics restart —
        // proving or disproving the toast as the source of a reported frame spike is the whole point.
        if (ms > SlowShowMs) _log($"[NoticeTips] slow show {ms}ms");
    }

    private void Invoke(string chunk)
    {
        if (_runChunk is not null) { _runChunk(chunk); return; }
        _luaDoString!.Invoke(_luaState, new object[] { chunk, "stellar.noticetips" });
    }

    private bool EnsureInvoker()
    {
        if (_runChunk is not null) return true;
        EnsureLuaState();
        return _luaDoString is not null;
    }

    private static long ElapsedMs(long sinceStamp)
        => (Stopwatch.GetTimestamp() - sinceStamp) * 1000L / Stopwatch.Frequency;

    // Thread-safe: builds the chunk and enqueues it; Tick() dispatches on the main thread.
    internal void Execute(LuaNoticeTipBuilder b) => _pending.Enqueue((BuildChunk(b), WindowMsOf(b)));

    // How long this tip stays on screen (delay + duration) — the repeat gate's window. Clamped so a
    // caller cannot disable the gate with a 0 s tip or hold it open for a whole session.
    private static int WindowMsOf(LuaNoticeTipBuilder b)
    {
        var ms = (int)((b.Delay + b.Duration) * 1000f);
        return ms < MinWindowMs ? MinWindowMs : ms > MaxWindowMs ? MaxWindowMs : ms;
    }

    private static string BuildChunk(LuaNoticeTipBuilder b)
    {
        string  content        = LuaStr(b.Content);
        float   delay          = b.Delay;
        float   duration       = b.Duration;
        int     luaType        = ToLuaType(b.Type);
        string? audioEvent     = ResolveAudioEvent(b);
        bool    suppressDefault = b.Audio == NoticeTipAudio.Silent || audioEvent != null;

        return b.Type switch
        {
            NoticeTipType.GreenBar or NoticeTipType.RedBar =>
                BuildPopChunk(content, delay, duration, luaType, audioEvent),

            NoticeTipType.PopTip =>
                BuildPopTipChunk(b, content, audioEvent),

            _ => BuildTopPopChunk(b, content, luaType, suppressDefault, audioEvent),
        };
    }

    // SHOW the pop view for a tip that was just enqueued — the game's OWN append path.
    //
    // noticetip_pop drains data.pop_msg_data ITSELF: OnRefresh dequeues one item when it has no viewData
    // (noticetip_pop_view.lua:80-86), showPopTip holds at most three at once and re-enqueues the overflow
    // (:130-133), and each item's OnEnd dequeues the next (:210-214). So a SECOND tip only needs the live
    // view refreshed — not re-opened.
    //
    // Z.UIMgr:OpenView on an ALREADY-OPEN view is not cheap (ui_manager.lua:112-161): GetView returns the
    // cached instance (:114), then the list is re-ordered (:137-145), Z.UICameraHelper:OpenUICamera runs
    // (:151), ui:Active (:152 → ui_base.lua:45-59) calls SetAsLastSibling (:53 → ui_view_base.lua:80-85 =
    // a transform re-parent to last sibling PLUS Z.UIMgr:UpdateDepth over the layer, i.e. a canvas
    // rebuild) before it ever reaches OnRefresh (:55), and finally ViewStatusSwitchMgr:TrySetStateActive
    // (:158) and a global EventMgr:Dispatch(UIOpen) (:160) fire. Owner report 2026-09-05: five toasts over
    // ~3-5 s = five of those, measured as 100-185 ms frames.
    //
    // So: refresh the LIVE view when there is one (SetViewData(nil) + the same CallLifeCycleFunc(OnRefresh)
    // that Active would have reached — ui_base.lua:49,55), and pay the full OpenView only when there is no
    // usable view. The fallback keeps this correct on the first-ever tip and after the view is torn down
    // (DeActiveAll on a scene switch), so no tip can be stranded in the queue.
    private const string ShowPopView =
        " local v=Z.UIMgr:GetView('noticetip_pop')" +
        " if v and v.IsActive and v.IsLoaded and v.IsVisible then v:SetViewData(nil); v:CallLifeCycleFunc(v.OnRefresh)" +
        " else Z.UIMgr:OpenView('noticetip_pop') end";

    // Green/Red bars — EnqueuePopData path (type2AudioTable never fires in this path)
    private static string BuildPopChunk(string content, float delay, float duration, int luaType, string? audioEvent)
    {
        string play = audioEvent != null ? $" Z.AudioMgr:Play('{LuaStr(audioEvent)}')" : "";
        return
            "pcall(function()" +
            " local data=Z.DataMgr.Get('noticetip_data')" +
           $" local cfg={{Id=0,Delay={F(delay)},DurationTime={F(duration)},Audio='',RepeatPlay={{1,0}},Type=10}}" +
           $" local info={{config=cfg,content='{content}',viewType={luaType}}}" +
            " data:EnqueuePopData(info)" +
            ShowPopView +
            play +
            " end)";
    }

    // PopTip — EnqueuePopData path, audio driven by config.Audio field
    private static string BuildPopTipChunk(LuaNoticeTipBuilder b, string content, string? audioEvent)
    {
        string audio = b.Audio == NoticeTipAudio.Default ? "" : LuaStr(audioEvent ?? "");
        int    rc    = Math.Max(1, b.RepeatCount);
        int    ri    = (int)b.RepeatIntervalMs;
        return
            "pcall(function()" +
            " local data=Z.DataMgr.Get('noticetip_data')" +
           $" local cfg={{Id=0,Delay={F(b.Delay)},DurationTime={F(b.Duration)},Audio='{audio}',RepeatPlay={{{rc},{ri}}},Type=10}}" +
           $" local info={{config=cfg,content='{content}',viewType=1}}" +
            " data:EnqueuePopData(info)" +
            ShowPopView +
            " end)";
    }

    // Special/Win/Fail — EnqueueTopPopData path, audio fires from type2AudioTable in OnRefresh
    private static string BuildTopPopChunk(LuaNoticeTipBuilder b, string content, int luaType, bool suppressDefault, string? audioEvent)
    {
        string play = audioEvent != null ? $" Z.AudioMgr:Play('{LuaStr(audioEvent)}')" : "";

        if (!suppressDefault)
        {
            return
                "pcall(function()" +
                " local data=Z.DataMgr.Get('noticetip_data')" +
               $" local cfg={{Id=0,Delay={F(b.Delay)},DurationTime={F(b.Duration)},Audio='',RepeatPlay={{1,0}}}}" +
               $" local info={{config=cfg,content='{content}',viewType={luaType}}}" +
                " data:EnqueueTopPopData(info)" +
                " end)";
        }

        // Silent or custom audio: monkey-patch OnRefresh on the cached view instance before
        // triggering, so the type2AudioTable:Play() call is skipped for this one invocation.
        // On first-ever call the view may not be cached yet — suppression won't apply that once.
        return
            "pcall(function()" +
            " local data=Z.DataMgr.Get('noticetip_data')" +
           $" local cfg={{Id=0,Delay={F(b.Delay)},DurationTime={F(b.Duration)},Audio='',RepeatPlay={{1,0}}}}" +
           $" local info={{config=cfg,content='{content}',viewType={luaType}}}" +
            " local v=Z.UIMgr:GetView('noticetip_pop')" +
            " local orig" +
            " if v then" +
            "  orig=v.OnRefresh" +
            "  v.OnRefresh=function(self)" +
            "   self.OnRefresh=orig" +
            "   if self.viewData then" +
            "    local vt=self.viewData.viewType" +
            "    if vt==(E.TipsType).DungeonChallengeWinTips then self:PopDungeonEndTips(true)" +
            "    elseif vt==(E.TipsType).DungeonChallengeFailTips then self:PopDungeonEndTips(false)" +
            "    elseif vt==(E.TipsType).DungeonSpecialTips then self:PopDungeonSpTips() end" +
            "   else local m=self.data_:DequeuePopData() if m then self:showPopTip(m) end end" +
            "  end" +
            " end" +
            " data:EnqueueTopPopData(info)" +
            play +
            " end)";
    }

    private static int ToLuaType(NoticeTipType t) => t switch
    {
        NoticeTipType.GreenBar   => 12,
        NoticeTipType.RedBar     => 11,
        NoticeTipType.Special    => 5,
        NoticeTipType.WinBanner  => 6,
        NoticeTipType.FailBanner => 7,
        _                        => 1,
    };

    private static string? ResolveAudioEvent(LuaNoticeTipBuilder b)
    {
        if (b.CustomAudio != null) return b.CustomAudio;
        return b.Audio switch
        {
            NoticeTipAudio.MagicA         => "UI_Event_Magic_A",
            NoticeTipAudio.ErrorTip       => "UI_Event_Error_Tip",
            NoticeTipAudio.NoticeTip      => "UI_Event_Notice_Tip",
            NoticeTipAudio.DungeonVictory => "UI_Event_Dungeon_Victory",
            NoticeTipAudio.DungeonFail    => "UI_Event_Dungeon_Fail",
            _                             => null,
        };
    }

    private static string LuaStr(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "");

    private static string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void EnsureLuaState()
    {
        if (_luaDoString != null) return;

        var lsType = FindType("LuaInterface.LuaState");
        if (lsType != null)
        {
            _luaState =
                lsType.GetProperty("mainState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
                ?? lsType.GetField("mainState",  BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        }

        if (_luaState is null)
        {
            var clientType = FindType("LuaClient");
            var inst = clientType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (inst != null)
            {
                var t = inst.GetType();
                _luaState =
                    t.GetProperty("luaState", BindingFlags.Instance | BindingFlags.Public)?.GetValue(inst)
                    ?? t.GetField("luaState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(inst);
            }
        }

        if (_luaState != null)
        {
            foreach (var m in _luaState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "DoString" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length < 2 || ps[0].ParameterType != typeof(string)) continue;
                if (m.ReturnType == typeof(void)) { _luaDoString = m; break; }
            }
        }

        _log($"[NoticeTips] LuaState={(_luaState != null ? "ok" : "null")} DoString={(_luaDoString != null ? "ok" : "null")}");
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t is not null) return t;
        }
        return null;
    }
}

internal sealed class LuaNoticeTipBuilder : INoticeTipBuilder
{
    private readonly NoticeTipService _svc;

    internal NoticeTipType  Type             { get; }
    internal string         Content          { get; private set; } = "";
    internal NoticeTipAudio Audio            { get; private set; } = NoticeTipAudio.Default;
    internal string?        CustomAudio      { get; private set; }
    internal float          Duration         { get; private set; } = 3f;
    internal float          Delay            { get; private set; } = 0.2f;
    internal int            RepeatCount      { get; private set; } = 1;
    internal float          RepeatIntervalMs { get; private set; } = 0f;

    internal LuaNoticeTipBuilder(NoticeTipService svc, NoticeTipType type)
    {
        _svc = svc;
        Type = type;
    }

    public INoticeTipBuilder WithContent(string content)                          { Content = content; return this; }
    public INoticeTipBuilder WithAudio(NoticeTipAudio audio)                      { Audio = audio; CustomAudio = null; return this; }
    public INoticeTipBuilder WithAudio(string customEventName)                    { CustomAudio = customEventName; return this; }
    public INoticeTipBuilder WithDuration(float seconds)                          { Duration = seconds; return this; }
    public INoticeTipBuilder WithDelay(float seconds)                             { Delay = seconds; return this; }
    public INoticeTipBuilder WithRepeat(int count, float intervalMs = 0f)         { RepeatCount = count; RepeatIntervalMs = intervalMs; return this; }
    public void Show() => _svc.Execute(this);
}
