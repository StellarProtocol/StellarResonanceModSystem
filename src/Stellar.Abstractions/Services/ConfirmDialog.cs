using System;

namespace Stellar.Abstractions.Services;

/// <summary>How serious a confirmation is — picks the icon tile and the confirm pill colour (house severity ladder).</summary>
public enum ConfirmSeverity
{
    /// <summary>A normal action (accent pill).</summary>
    Primary = 0,
    /// <summary>Resets the player's own choices, recoverable by hand (amber pill).</summary>
    Caution = 1,
    /// <summary>Loses data (red pill).</summary>
    Danger = 2,
}

/// <summary>One question for a <see cref="ConfirmController"/> (framework 2.33.0). Pass already-localized texts.
/// Every option is an <c>init</c> property, so more can be added without breaking compiled plugins.</summary>
/// <param name="Title">The bold question, e.g. "Delete all 12 runs?".</param>
/// <param name="Message">The body text in the card's message box.</param>
/// <param name="OnConfirm">Runs after the dialog closed, when the player confirms.</param>
public sealed record ConfirmRequest(string Title, string Message, Action OnConfirm)
{
    /// <summary>Muted caption under the title, shown UPPERCASE (e.g. the window or feature name). Null = none.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Optional bold line under the message (e.g. "This can't be undone."). Null = none.</summary>
    public string? Emphasis { get; init; }

    /// <summary>Label of the confirm pill. Null = the framework's localized "OK".</summary>
    public string? ConfirmLabel { get; init; }

    /// <summary>Label of the cancel pill. Null = the framework's localized "Cancel".</summary>
    public string? CancelLabel { get; init; }

    /// <summary>Colour of the icon tile and the confirm pill. Default <see cref="ConfirmSeverity.Danger"/>.</summary>
    public ConfirmSeverity Severity { get; init; } = ConfirmSeverity.Danger;

    /// <summary>Non-null = offer a "Don't ask again" checkbox (off when the dialog opens). Confirming with it ticked stores
    /// this key in <see cref="ConfirmController.DontAskStore"/>; from then on <see cref="ConfirmController.Show"/> runs
    /// <see cref="OnConfirm"/> at once without opening. Null (default) = no checkbox. Don't use it for deletes.</summary>
    public string? DontAskAgainKey { get; init; }

    /// <summary>Runs after the dialog closed without confirming (Cancel, Esc, the window's ✕, the window or the host
    /// being hidden, or a newer request replacing this one). Null = nothing.</summary>
    public Action? OnCancel { get; init; }
}

/// <summary>
/// The state of one window's modal confirm (framework 2.33.0). Create one per window, hand it to a
/// <see cref="ConfirmHostElement"/> wrapped around the content it must cover, and call <see cref="Show"/>.
/// <para>While a request is open the framework draws a dark scrim over the host's content with a centred card (icon tile,
/// title, subtitle, message, Cancel + confirm pills) and the content underneath takes NO input — clicks, hover, wheel and
/// drags are all swallowed. Whatever is outside the host (the title bar, its ✕, window dragging) keeps working.
/// Esc = Cancel; Enter does nothing. Hiding the window (✕) or the host cancels the open request.</para>
/// <para>Main thread only (call it from UI callbacks / OnUpdate). Callbacks run after the dialog has closed, so one may
/// open the next confirm.</para>
/// </summary>
public sealed class ConfirmController
{
    private const string KeyPrefix = "confirm.dontAsk.";

    /// <summary>Where "Don't ask again" choices persist — pass a section of your own plugin config (e.g.
    /// <c>Config.GetSection("ui")</c>), so they are per plugin. Null = the choice lasts for this session only.</summary>
    public IConfigSection? DontAskStore { get; init; }

    private System.Collections.Generic.HashSet<string>? _sessionDontAsk;

    /// <summary>The open request, or null.</summary>
    public ConfirmRequest? Current { get; private set; }

    /// <summary>A request is open (the scrim + card are shown).</summary>
    public bool IsOpen => Current != null;

    /// <summary>The "Don't ask again" checkbox; reset to false whenever a request opens.</summary>
    public bool DontAskChecked { get; set; }

    /// <summary>Opens <paramref name="request"/>. Returns false when it did not open because the player chose "Don't ask
    /// again" for its key earlier — <see cref="ConfirmRequest.OnConfirm"/> has then already run. A request still open is
    /// cancelled first (its OnCancel runs).</summary>
    public bool Show(ConfirmRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.DontAskAgainKey is { } key && IsDontAsk(key))
        {
            Cancel();
            request.OnConfirm();
            return false;
        }
        Cancel();
        Current = request;
        DontAskChecked = false;
        return true;
    }

    /// <summary>The player confirmed: closes, stores "Don't ask again" when ticked, then runs OnConfirm. No-op when closed.</summary>
    public void Confirm()
    {
        var r = Current;
        if (r == null) return;
        Current = null;
        if (DontAskChecked && r.DontAskAgainKey is { } key) SetDontAsk(key, true);
        DontAskChecked = false;
        r.OnConfirm();
    }

    /// <summary>Closes without confirming, then runs OnCancel. No-op when closed.</summary>
    public void Cancel()
    {
        var r = Current;
        if (r == null) return;
        Current = null;
        DontAskChecked = false;
        r.OnCancel?.Invoke();
    }

    /// <summary>True when the player chose "Don't ask again" for <paramref name="key"/>.</summary>
    public bool IsDontAsk(string key)
        => DontAskStore != null ? DontAskStore.Get(KeyPrefix + key, false) : _sessionDontAsk?.Contains(key) == true;

    /// <summary>Sets or clears a "Don't ask again" choice (e.g. a settings "Reset confirmations" button passes false).</summary>
    public void SetDontAsk(string key, bool on)
    {
        if (DontAskStore != null)
        {
            DontAskStore.Set(KeyPrefix + key, on);
            DontAskStore.Save();
            return;
        }
        _sessionDontAsk ??= new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        if (on) _sessionDontAsk.Add(key); else _sessionDontAsk.Remove(key);
    }
}

/// <summary>Covers <paramref name="Content"/> with <paramref name="Controller"/>'s modal confirm while one is open
/// (framework 2.33.0). Wrap the part of the window the dialog must lock — usually everything below your header — once.
/// Built on <see cref="LayerStackElement"/>: the content lays out exactly as without the host.</summary>
/// <param name="Content">The window content underneath.</param>
/// <param name="Controller">The window's confirm state.</param>
public sealed record ConfirmHostElement(HudElement Content, ConfirmController Controller) : HudElement
{
    /// <summary>Fixed label width of the confirm pill in px, shared by every request on this host — give the widest
    /// confirm label in any language, so the pill never changes size. 0 (default) = 96. (The Cancel pill is fixed by the
    /// framework for its own "Cancel" in every language.)</summary>
    public float ConfirmLabelWidth { get; init; }
}
