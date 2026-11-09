using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;
using UnityEngine;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// Save / Discard for the "Edit colours" dialog (user-approved 2026-11-09). Edits stay LIVE and keep writing through to
// IThemeOverrides + config exactly as before; a ThemeOverrideSnapshot taken when the dialog opens (and on every Save)
// is the baseline: Discard puts every differing slot back (incl. added / removed overrides) and persists that too.
// The footer row: status ("No changes" muted / "N unsaved changes" Caution) + Discard (Neutral) + Save (Primary), both
// RECESSED-disabled while nothing changed (SDL Rule 22). Closing the dialog — or anything that would close / retarget
// it (ThemesPanel.GuardUnsaved) — with changes turns the SAME row into the inline confirm "Unsaved changes" + Save /
// Discard / Keep editing. Both rows are the same 36 px bubble (pills + 4 px padding), so the window never changes height.
// Discard is NEUTRAL, not Danger: the severity ladder (SDL R8) keeps Danger for destructive / everyone-interrupting
// actions, and this only returns the player's own theme to how it was a moment ago.
internal sealed partial class ThemeEditorBody
{
    // Pill label boxes = the LONGEST translation of each label (bold 13, measured with fontTools on the shipped Stellar UI
    // Thai Bold / Yu Gothic UI Bold + Malgun Gothic Bold, 2026-11-09), so no locale ever resizes a pill (R10). They are
    // per-label, not one shared width: the inner row is only 400 px (440 window − 2×12 body − 2×8 bubble), and the old
    // shared 56/80 boxes left the bold question ~118 px — "Unsaved chan…" in English. Now the three confirm pills take
    // 255 + 3×6 gaps, leaving the question 127 px (longest translation: en "Unsaved changes" 122); the normal row leaves
    // the status 239 px (longest: fil "99 pagbabagong di pa naka-save" 218).
    private const float SaveLabel = 49f;             // longest: id "Simpan" 48.5
    private const float DiscardLabel = 52f;          // longest: ko "되돌리기" 52.0
    private const float KeepEditingLabel = 82f;      // longest: en "Keep editing" 81.9
    private const float FooterPadY = 4f;
    private const float FooterPadX = 8f;
    private const float FeedbackSeconds = 3f;

    private ThemeOverrideSnapshot? _session;
    private int _unsaved;
    private bool _askUnsaved;
    private Action<bool?> _resolveAsk = _ => { };
    private string? _sessionFeedback;
    private bool _feedbackGood;
    private float _feedbackUntil;

    private static readonly IReadOnlyList<ColorSlotInfo> NoSlots = Array.Empty<ColorSlotInfo>();

    /// <summary>The custom theme the open dialog's baseline belongs to (null = no session).</summary>
    internal string? SessionTheme => _session?.Theme;

    /// <summary>Slots that differ from the baseline (refreshed by <see cref="TickSession"/>).</summary>
    internal int UnsavedCount => _unsaved;

    internal bool HasUnsaved => _session != null && _unsaved > 0;

    /// <summary>Whether the footer currently shows the "Unsaved changes" confirm.</summary>
    internal bool IsAsking => _askUnsaved;

    // The cached slot list (SlotAt refreshes it on a late registration / relabel) — no per-tick Slots allocation.
    private IReadOnlyList<ColorSlotInfo> CurrentSlots()
    {
        SlotAt(0);
        return _slotCache ?? NoSlots;
    }

    /// <summary>Takes the baseline for the active custom theme (the dialog opened, or the active theme changed under it).</summary>
    internal void BeginSession()
    {
        _askUnsaved = false;
        _sessionFeedback = null;
        _session = _namedTheme.ActiveCustomName is { } t ? ThemeOverrideSnapshot.Capture(t, _overrides, CurrentSlots()) : null;
        _unsaved = 0;
    }

    internal void EndSession()
    {
        _session = null;
        _unsaved = 0;
        _askUnsaved = false;
        _sessionFeedback = null;
    }

    /// <summary>Recounts the unsaved slots (dialog tick, only while it is open).</summary>
    internal void TickSession()
    {
        _unsaved = _session != null && _namedTheme.ActiveCustomName == _session.Theme
            ? _session.CountChanges(_overrides, CurrentSlots()) : 0;
        if (_unsaved == 0) _askUnsaved = false;   // nothing left to ask about (e.g. reset back by hand)
    }

    /// <summary>Shows the inline "Unsaved changes" confirm; <paramref name="resolve"/> gets true after Save, false after
    /// Discard, null after Keep editing.</summary>
    internal void AskUnsaved(Action<bool?> resolve)
    {
        _resolveAsk = resolve;
        _askUnsaved = true;
    }

    /// <summary>Keeps what's shown: persists it and makes it the new baseline.</summary>
    internal void SaveSession()
    {
        if (_editDirty) { _namedTheme.NotifyColorsChanged(); _editDirty = false; }
        _overrides.Flush();
        BeginSession();
    }

    /// <summary>Back to the baseline: restore every differing slot, persist, re-skin live.</summary>
    internal void DiscardSession()
    {
        if (_session == null || _namedTheme.ActiveCustomName != _session.Theme) { BeginSession(); return; }
        _session.Restore(_overrides, CurrentSlots());
        _overrides.Flush();
        _namedTheme.NotifyColorsChanged();
        _editDirty = false;
        // A discarded ADDED plugin override leaves the list: don't keep the picker bound to it.
        if (_expandedSlotKey is { } k)
        {
            if (!_overrides.HasOverride(k) && !k.StartsWith(SystemOwner, StringComparison.Ordinal)) _expandedSlotKey = null;
            else _hexBuffer = ToHex(_overrides.Resolve(k));
        }
        _unsaved = 0;
        _askUnsaved = false;
    }

    /// <summary>A rename keeps the same overrides under a new name — carry the baseline over.</summary>
    private void RenameSession(string from, string to)
    {
        if (_session != null && _session.Theme == from) _session.Theme = to;
    }

    private void FooterSave()
    {
        SaveSession();
        Feedback(_text.T("colours.fb.saved"), good: true);
    }

    private void FooterDiscard()
    {
        DiscardSession();
        BeginSession();
        Feedback(_text.T("colours.fb.discarded"), good: false);
    }

    private void Feedback(string text, bool good)
    {
        _sessionFeedback = good ? "✓ " + text : text;   // plain-text "✓" (bold ✓ is untrusted, SDL §6)
        _feedbackGood = good;
        _feedbackUntil = Time.unscaledTime + FeedbackSeconds;
    }

    private void ResolveAsk(bool save)
    {
        if (save) SaveSession(); else DiscardSession();
        _askUnsaved = false;
        _resolveAsk(save);
    }

    private void KeepEditing()
    {
        _askUnsaved = false;
        _resolveAsk(null);
    }

    private string FooterStatus()
    {
        if (_sessionFeedback != null && Time.unscaledTime < _feedbackUntil && _unsaved == 0) return _sessionFeedback;
        _sessionFeedback = null;
        if (_unsaved == 0) return _text.T("colours.footer.none");
        return _text.TFormat(_unsaved == 1 ? "colours.footer.one" : "colours.footer.many", _unsaved);
    }

    private ColorRgba? FooterStatusColor()
    {
        var s = _k.S;
        if (_unsaved > 0) return s.Readable(s.Caution(), s.Background());
        if (_sessionFeedback != null) return _feedbackGood ? s.Readable(s.Success(), s.Background()) : s.Muted();
        return s.Muted();
    }

    // The footer: the normal row and the confirm row are the SAME bubble geometry (28 px pills + FooterPadY), so the
    // swap never moves anything. Normal = transparent fill; confirm = the panel fill, like the Themes delete confirm.
    private HudElement BuildFooter()
    {
        var s = _k.S;
        bool En() => _unsaved > 0;
        var normal = FooterBubble(new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(FooterStatus, Color: FooterStatusColor, NoWrap: true) { Ellipsize = true },
                Weight: 1f),
            s.NeutralButton(_k.T("common.discard"), FooterDiscard, DiscardLabel, En),
            s.PrimaryButton(_k.T("common.save"), FooterSave, SaveLabel, En),
        }, Gap: 6f), () => StellarStyle.Transparent);
        var confirm = FooterBubble(new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(_k.T("colours.unsaved.q"), Color: () => s.Text(), NoWrap: true)
                { Bold = true, Ellipsize = true }, Weight: 1f),
            s.PrimaryButton(_k.T("common.save"), () => ResolveAsk(save: true), SaveLabel),
            s.NeutralButton(_k.T("common.discard"), () => ResolveAsk(save: false), DiscardLabel),
            s.NeutralButton(_k.T("colours.keepEditing"), KeepEditing, KeepEditingLabel),
        }, Gap: 6f), () => s.Fill(s.Panel()));
        return new ConditionalElement(() => _askUnsaved, confirm, normal);
    }

    private static HudElement FooterBubble(HudElement row, Func<ColorRgba> fill) => new BubbleElement(row)
    {
        FillColorFn = () => fill(), CornerRadius = StellarStyle.TileRadius, PaddingX = FooterPadX, PaddingY = FooterPadY,
    };
}
