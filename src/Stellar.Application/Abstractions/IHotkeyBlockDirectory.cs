namespace Stellar.Application.Abstractions;

/// <summary>
/// Global "block all hotkeys from game" control, separated from <see cref="IHotkeyDirectory"/>
/// to stay within the 8-member interface limit enforced by STELLAR0005.
/// </summary>
internal interface IHotkeyBlockDirectory
{
    /// <summary>When true, all bound hotkey keys are suppressed from reaching the game.</summary>
    void SetBlockAllFromGame(bool block);

    /// <summary>Returns the current global block state.</summary>
    bool GetBlockAllFromGame();
}
