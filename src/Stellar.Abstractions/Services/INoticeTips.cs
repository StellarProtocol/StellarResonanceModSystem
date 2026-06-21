namespace Stellar.Abstractions.Services;

/// <summary>The visual style of the noticetip bar to display.</summary>
public enum NoticeTipType
{
    /// <summary>Green horizontal bar at the top of the screen (DungeonGreenTips).</summary>
    GreenBar,
    /// <summary>Red horizontal bar at the top of the screen (DungeonRedTips).</summary>
    RedBar,
    /// <summary>Special dungeon banner (DungeonSpecialTips).</summary>
    Special,
    /// <summary>Victory banner (DungeonChallengeWinTips).</summary>
    WinBanner,
    /// <summary>Failure banner (DungeonChallengeFailTips).</summary>
    FailBanner,
    /// <summary>Small stacking pop-up notification (PopTip).</summary>
    PopTip,
}

/// <summary>Audio event to play when the noticetip appears.</summary>
public enum NoticeTipAudio
{
    /// <summary>Play whatever audio the tip type plays by default.</summary>
    Default,
    /// <summary>Suppress all audio for this tip.</summary>
    Silent,
    /// <summary>UI_Event_Magic_A (default green bar sound).</summary>
    MagicA,
    /// <summary>UI_Event_Error_Tip (default red bar sound).</summary>
    ErrorTip,
    /// <summary>UI_Event_Notice_Tip (default special/bottom sound).</summary>
    NoticeTip,
    /// <summary>UI_Event_Dungeon_Victory (default win banner sound).</summary>
    DungeonVictory,
    /// <summary>UI_Event_Dungeon_Fail (default fail banner sound).</summary>
    DungeonFail,
}

/// <summary>Fluent builder for a single noticetip display request.</summary>
public interface INoticeTipBuilder
{
    /// <summary>Text shown on the bar or pop-up.</summary>
    INoticeTipBuilder WithContent(string content);
    /// <summary>Audio to play when the tip appears.</summary>
    INoticeTipBuilder WithAudio(NoticeTipAudio audio);
    /// <summary>Raw Wwise event name to play (overrides any <see cref="NoticeTipAudio"/> setting).</summary>
    INoticeTipBuilder WithAudio(string customEventName);
    /// <summary>How long the tip stays visible (seconds). Default 3.</summary>
    INoticeTipBuilder WithDuration(float seconds);
    /// <summary>Delay before the tip appears (seconds). Default 0.2.</summary>
    INoticeTipBuilder WithDelay(float seconds);
    /// <summary>Repeat the tip N times with an optional gap (ms between repeats). Only applies to <see cref="NoticeTipType.PopTip"/>.</summary>
    INoticeTipBuilder WithRepeat(int count, float intervalMs = 0f);
    /// <summary>Fire the tip immediately with the accumulated settings.</summary>
    void Show();
}

/// <summary>
/// Framework service for triggering the game's noticetip system (dungeon bars, win/fail banners,
/// pop-up stacking tips) from any plugin, with full control over content, duration, and audio.
/// </summary>
public interface INoticeTips
{
    /// <summary>Create a builder for the given tip type. Call <see cref="INoticeTipBuilder.Show"/> to display it.</summary>
    INoticeTipBuilder Create(NoticeTipType type);
}
