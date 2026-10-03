namespace Stellar.Infrastructure.Game;

/// <summary>The book of the scene freeze's global time pause (Photo Studio scene-stays spec, amendment 2026-10-02 late;
/// devkit recon <c>free-camera-recon.md</c> § Run 9): the game's whole update loop, skill timelines, effects and animation
/// run on Unity's SCALED time, so <c>Time.timeScale = 0</c> stops all of them at once, the local player included.
/// <para><b>Save / hold / restore.</b> <see cref="Begin"/> keeps the value found at the press. While paused, every write the
/// game makes is a WISH (the hit-stop / slow-motion shows: <c>ZTimeScaleShowInfo.OnStop</c> writes 1.0 unconditionally):
/// <see cref="NoteGameWrite"/> keeps the latest non-zero one and tells the setter not to run, so the clock stays at 0.
/// <see cref="End"/> answers the value to put back — the latest wish, else the saved value — and never 0 or less (that
/// would leave the game paused with nobody holding the pause): then 1.</para>
/// <para><b>Watchdog</b> (a lost pause must never leave the game stopped): <see cref="Verify"/> answers, at the framework's
/// unscaled tick, whether the clock drifted off 0 by a write the setter hook did not see (the caller re-asserts 0 and the
/// drift counts as a wish), and <see cref="Stalled"/> whether that tick stopped beating (then <see cref="TimePauseWatchdog"/>
/// releases the whole freeze). A stall must PERSIST — over <see cref="StallSeconds"/> without a beat, seen on at least
/// <see cref="StallFrames"/> paused frames spanning <see cref="StallGraceSeconds"/> — so one long main-thread hitch (the
/// first frame after it is past the limit before its own tick can beat) is never a stall (qa M-8). Pure (unit-tested); main
/// thread.</para></summary>
internal sealed class ClockPauseState
{
    /// <summary>The value put back when neither the saved value nor a wish is a running clock.</summary>
    internal const float Running = 1f;

    /// <summary>Real seconds without a watchdog beat, while paused, before the pause counts as lost.</summary>
    internal const float StallSeconds = 10f;

    /// <summary>Real seconds a stall must persist, once past <see cref="StallSeconds"/>, before it counts.</summary>
    internal const float StallGraceSeconds = 1f;

    /// <summary>Paused frames a stall must be seen on, once past <see cref="StallSeconds"/>, before it counts.</summary>
    internal const int StallFrames = 3;

    private float _saved = Running;
    private float? _wanted;
    private float _lastBeat;
    private float _overSince;
    private int _overFrames;

    /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
    public bool Paused { get; private set; }

    /// <summary>The value found at the press.</summary>
    public float Saved => _saved;

    /// <summary>The latest non-zero value the game wrote while paused, or null.</summary>
    public float? Wanted => _wanted;

    /// <summary>Game writes held at 0 during this pause (the setter hook).</summary>
    public int HeldWrites { get; private set; }

    /// <summary>Drifts off 0 the hook did not see, found and re-asserted by <see cref="Verify"/> during this pause.</summary>
    public int Bypassed { get; private set; }

    /// <summary>Starts a pause at real time <paramref name="now"/> with <paramref name="current"/> the clock's value. False
    /// (nothing changes) when already paused.</summary>
    public bool Begin(float current, float now)
    {
        if (Paused) return false;
        Paused = true;
        _saved = current;
        _wanted = null;
        HeldWrites = Bypassed = 0;
        Beat(now);
        return true;
    }

    /// <summary>The setter hook's question for one write of <paramref name="value"/>: true = let it run. Not paused: always
    /// true. Paused: a write of 0 runs (it keeps the pause); any other value is kept as the wish and held (false).</summary>
    public bool NoteGameWrite(float value)
    {
        if (!Paused || value == 0f) return true;
        _wanted = value;
        HeldWrites++;
        return false;
    }

    /// <summary>The watchdog's clock check: true when the clock reads <paramref name="observed"/> ≠ 0 while paused — a write
    /// the hook did not see. The caller writes 0 again; a positive drift is kept as the wish.</summary>
    public bool Verify(float observed)
    {
        if (!Paused || observed == 0f) return false;
        if (observed > 0f) _wanted = observed;
        Bypassed++;
        return true;
    }

    /// <summary>What the watchdog found at one framework tick (<see cref="Watch"/>).</summary>
    internal enum WatchVerdict
    {
        /// <summary>Not paused, or paused and everything holds.</summary>
        Ok,

        /// <summary>The clock had drifted off 0 by a write the hook did not see: the caller writes 0 again.</summary>
        Reasserted,

        /// <summary>Paused but the freeze no longer holds it (its bookkeeping lost the pause): the caller resumes the clock.</summary>
        LostPause,

        /// <summary>Paused but the world is gone (a scene end the release path missed): the caller releases the scene.</summary>
        LeftWorld,
    }

    /// <summary>The watchdog at one framework tick (real time <paramref name="now"/>; the tick runs unscaled while paused):
    /// beats, then checks — in this order — that the freeze still holds the pause, that the world is still there, and that
    /// the clock still reads 0 (<paramref name="observed"/>).</summary>
    public WatchVerdict Watch(bool holderFrozen, bool worldActive, float observed, float now)
    {
        if (!Paused) return WatchVerdict.Ok;
        Beat(now);
        if (!holderFrozen) return WatchVerdict.LostPause;
        if (!worldActive) return WatchVerdict.LeftWorld;
        return Verify(observed) ? WatchVerdict.Reasserted : WatchVerdict.Ok;
    }

    /// <summary>The framework's tick beat at real time <paramref name="now"/> (it runs on unscaled time while paused).</summary>
    public void Beat(float now)
    {
        _lastBeat = now;
        _overFrames = 0;
    }

    /// <summary>One paused frame's stall check, AFTER that frame's tick had its chance to beat: true when paused and no
    /// <see cref="Beat"/> came for <see cref="StallSeconds"/> of real time, on at least <see cref="StallFrames"/> checks
    /// spanning <see cref="StallGraceSeconds"/>.</summary>
    public bool Stalled(float now)
    {
        if (!Paused || now - _lastBeat <= StallSeconds) { _overFrames = 0; return false; }
        if (_overFrames++ == 0) _overSince = now;
        return _overFrames >= StallFrames && now - _overSince >= StallGraceSeconds;
    }

    /// <summary>Ends the pause: the value to write back (the latest wish, else the saved value; 1 when that is not above 0),
    /// or null when not paused.</summary>
    public float? End()
    {
        if (!Paused) return null;
        Paused = false;
        var restore = _wanted ?? _saved;
        return restore > 0f ? restore : Running;
    }
}
