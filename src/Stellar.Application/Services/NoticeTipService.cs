using System;
using System.Collections.Concurrent;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

public sealed class NoticeTipService : INoticeTips
{
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<string> _pending = new();
    private object? _luaState;
    private MethodInfo? _luaDoString;

    public NoticeTipService(Action<string> log) => _log = log;

    public INoticeTipBuilder Create(NoticeTipType type) => new LuaNoticeTipBuilder(this, type);

    // Called from the Unity main thread each frame — safe to invoke LuaState.DoString here.
    public void Tick()
    {
        if (_pending.IsEmpty) return;
        EnsureLuaState();
        if (_luaDoString is null) return;
        while (_pending.TryDequeue(out var chunk))
        {
            try { _luaDoString.Invoke(_luaState, new object[] { chunk, "stellar.noticetips" }); }
            catch (Exception ex) { _log($"[NoticeTips] Lua error: {ex.Message}"); }
        }
    }

    // Thread-safe: builds the chunk and enqueues it; Tick() dispatches on the main thread.
    internal void Execute(LuaNoticeTipBuilder b) => _pending.Enqueue(BuildChunk(b));

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
            " Z.UIMgr:OpenView('noticetip_pop')" +
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
            " Z.UIMgr:OpenView('noticetip_pop')" +
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
