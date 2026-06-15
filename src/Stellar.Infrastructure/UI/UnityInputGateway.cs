using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using UnityEngine;
using AbsResolution = Stellar.Abstractions.Domain.Resolution;

namespace Stellar.Infrastructure.UI;

internal sealed class UnityInputGateway : IInputGateway
{
    // Reusable list to avoid per-frame allocation. Cleared each TickPoll.
    private readonly List<StellarKeyCode> _pressedScratch = new(8);
    // Keys observed DOWN on the previous poll. We derive "pressed this tick" edges from the
    // level state (GetKey) + this set, NOT Input.GetKeyDown — because the framework now polls on
    // the throttled tick (~30 Hz), and GetKeyDown is true for only the single render frame the key
    // went down, which a sub-frame-rate poll would routinely MISS. Level+latch catches any key
    // held across at least one tick (~33 ms), which is every real human keypress.
    private readonly HashSet<StellarKeyCode> _downLastPoll = new();

    /// <summary>
    /// Sink for one-shot diagnostic logging. Set by Host wiring once at boot.
    /// Logs every captured keypress + modifier flags so we can confirm the
    /// gateway is actually seeing user input. Logged at most once per frame.
    /// </summary>
    internal System.Action<string>? DiagnosticLog { get; set; }

    /// <summary>Polls key state for every StellarKeyCode and emits press EDGES via level+latch
    /// (reliable at the throttled tick rate; see <see cref="_downLastPoll"/>). Called once per tick.</summary>
    private bool _leftDownLastPoll;
    private bool _leftPressedSinceTick;

    public void TickPoll()
    {
        _pressedScratch.Clear();

        // Latch the left-mouse press edge off the button LEVEL so it survives the framework's throttled tick
        // rate — a GetMouseButtonDown single-frame edge would be missed on frames where the tick doesn't run.
        // True for one tick after the button transitions to down (a held drag is always caught; a sub-tick
        // click-release may be missed, acceptable for edit-mode grabs which hold to drag).
        bool leftDownNow;
        try { leftDownNow = Input.GetMouseButton(0); }
        catch { leftDownNow = false; }
        _leftPressedSinceTick = leftDownNow && !_leftDownLastPoll;
        _leftDownLastPoll = leftDownNow;
        foreach (var key in CommonHotkeyCodes)
        {
            bool downNow;
            try { downNow = Input.GetKey((KeyCode)key); }
            catch { continue; }   // input subsystem not ready during very early boot
            if (downNow)
            {
                if (_downLastPoll.Add(key)) _pressedScratch.Add(key);   // Add returns true => was not down last poll => edge
            }
            else
            {
                _downLastPoll.Remove(key);
            }
        }

        // Diagnostic surface: any time the gateway captures one or more keys,
        // emit a log line with the keys + current modifier flags. Useful for
        // diagnosing hotkey-not-firing complaints (e.g., F-keys eaten under
        // Wine/IL2CPP). Remove the DiagnosticLog assignment in BootstrapPlugin
        // once hotkeys are confirmed working.
        if (_pressedScratch.Count > 0 && DiagnosticLog is { } log)
        {
            try
            {
                var keys = string.Join(",", _pressedScratch);
                log($"[InputGateway] captured keys=[{keys}] modifiers={CurrentModifiers}");
            }
            catch { }
        }
    }

    public IReadOnlyList<StellarKeyCode> PressedKeysThisFrame => _pressedScratch;

    public ModifierKeys CurrentModifiers
    {
        get
        {
            var m = ModifierKeys.None;
            try
            {
                if (Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift))   m |= ModifierKeys.Shift;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= ModifierKeys.Ctrl;
                if (Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt))     m |= ModifierKeys.Alt;
            }
            catch { }
            return m;
        }
    }

    public AbsResolution CurrentResolution
    {
        get
        {
            try { return new AbsResolution(Screen.width, Screen.height); }
            catch { return new AbsResolution(1920, 1080); }
        }
    }

    public (float X, float Y) PointerPosition
    {
        get
        {
            try
            {
                var p = Input.mousePosition;
                // Convert from Unity's origin-bottom-left to origin-top-left to match GUI.matrix.
                return (p.x, Screen.height - p.y);
            }
            catch { return (0f, 0f); }
        }
    }

    public bool LeftMouseDown
    {
        get { try { return Input.GetMouseButton(0); } catch { return false; } }
    }

    public bool LeftMousePressedSinceTick => _leftPressedSinceTick;

    public int CurrentFrame
    {
        get { try { return Time.frameCount; } catch { return 0; } }
    }

    // The set of keys we poll each frame — anything bindable. Reading every
    // StellarKeyCode value would cover ~70 keys per frame; cheap (a few µs).
    private static readonly StellarKeyCode[] CommonHotkeyCodes;

    static UnityInputGateway()
    {
        var values = System.Enum.GetValues(typeof(StellarKeyCode));
        var list = new List<StellarKeyCode>(values.Length);
        foreach (StellarKeyCode v in values)
        {
            if (v != StellarKeyCode.None) list.Add(v);
        }
        CommonHotkeyCodes = list.ToArray();
    }
}
