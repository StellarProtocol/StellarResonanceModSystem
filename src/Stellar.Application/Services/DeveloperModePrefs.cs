using System;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// The framework's hidden "developer mode" flag (2.30.0): when on, the Settings hub shows its Developer tab (HUD path
/// dump, perf overlay switch) and the Shift+End perf-overlay hotkey works. Persisted in the framework config section
/// <see cref="SectionName"/> under <see cref="Key"/> — i.e. <c>stellar/plugins/stellar.framework.config.json</c> →
/// <c>"developer": { "developerMode": true }</c> — so a developer can also switch it on by editing the file. Off by
/// default: normal players never see the developer tools.
/// <para>The value is cached (the rail reads it every refresh); the host calls <see cref="Reload"/> when the config
/// file is edited externally, so a hand edit applies live.</para>
/// </summary>
internal sealed class DeveloperModePrefs
{
    public const string SectionName = "developer";
    public const string Key = "developerMode";

    private readonly IConfigSection _config;
    private bool _enabled;

    public DeveloperModePrefs(IConfigSection config)
    {
        _config = config;
        _enabled = config.Get(Key, false);
    }

    /// <summary>Raised (game thread) with the new value whenever the flag flips — from the UI or an external edit.</summary>
    public event Action<bool>? Changed;

    /// <summary>Whether developer mode is on. Setting it persists and raises <see cref="Changed"/> (no-op if equal).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            _config.Set(Key, value);
            // Quiet: our own write must not come back as an "external" change through SectionChanged → Reload.
            _config.SaveQuiet();
            Changed?.Invoke(value);
        }
    }

    /// <summary>Re-reads the persisted value (after an external file edit); raises <see cref="Changed"/> if it differs.</summary>
    public void Reload()
    {
        var now = _config.Get(Key, false);
        if (now == _enabled) return;
        _enabled = now;
        Changed?.Invoke(now);
    }
}

/// <summary>
/// The "click the version 7 times" unlock (Android build-number style) as pure logic: each <see cref="Click"/> counts
/// on from the previous one only if it came within <see cref="MaxGapSeconds"/>; a slower click starts over at 1.
/// Clock-agnostic (only deltas of the caller's seconds clock are used), so it unit-tests without Unity.
/// </summary>
internal sealed class DeveloperUnlockCounter
{
    public const int ClicksNeeded = 7;
    /// <summary>Longest pause between two clicks that still continues the sequence.</summary>
    public const double MaxGapSeconds = 1.5;
    /// <summary>From this click on, the feedback line counts down ("3 more clicks…").</summary>
    public const int CountdownFrom = 4;

    private int _count;
    private double _last = double.NegativeInfinity;

    /// <summary>Registers a click at <paramref name="now"/> (seconds) and returns the clicks still needed: 0 = this click
    /// completed the sequence (the counter then resets).</summary>
    public int Click(double now)
    {
        _count = now - _last <= MaxGapSeconds ? _count + 1 : 1;
        _last = now;
        if (_count < ClicksNeeded) return ClicksNeeded - _count;
        Reset();
        return 0;
    }

    public void Reset()
    {
        _count = 0;
        _last = double.NegativeInfinity;
    }
}
