using System;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Second half of the sticky cursor's world-click swallow, next to <see cref="RewiredMouseGate"/>. Disabling Rewired's
/// mouse stops attacks and targeting actions, but NOT world model-picking (clicking another player still opened the
/// avatar badge in-game, fw <c>637f0df</c>): picking runs through the game's touch layer, which turns the raw mouse into
/// a <c>ZTouch</c> itself and ignores Rewired.
/// <para>The gate is a PREFIX that skips <c>private void Panda.ZInput.ZTouchManager.simulateTouch(float)</c>
/// (dump.cs:156022) while armed, so no mouse touch is created or updated and nothing gets picked. Validated in-game
/// (fw <c>7921b27</c>, 3/3 swallowed clicks); the touch system recovers cleanly after disarm. Gating the pointer reads
/// upstream (<c>DefaultMousePointer.GetMouseButton*</c>) was tried and did NOT stop picking — see Knowledge Base
/// <c>Harmony-Rewired-Input-Patching.md</c>.</para>
/// <para>⚠️ simulateTouch also feeds the game's touch-driven UI, so this must only be armed while the pointer is over
/// the WORLD, never over game UI or a Stellar window. The caller guarantees that: it arms exactly when the Rewired gate
/// is armed (sticky + pointer over the world, held until the buttons are up, plus the up frame).</para>
/// <para>Patched lazily on first need (engage or first arm), never unpatched at runtime (a per-frame method; a runtime
/// unpatch risks the native trampoline crash). The framework's <see cref="IHarmonyHost"/> unpatches on teardown.
/// Unarmed, the prefix exits on one static bool. The target takes only a <c>float</c>, no in/ref struct.</para>
/// </summary>
internal sealed class WorldPointerGate : IDisposable
{
    private const string TouchTypeName = "Panda.ZInput.ZTouchManager";

    // Read by the prefix (main thread). Reset each arm, reported on disarm.
    private static bool _armed;
    private static int _touchSimSkipped;

    private readonly IHarmonyHost _host;
    private readonly IPluginLog _log;
    private Harmony? _harmony;
    private bool _touchPatched, _touchWarned;

    public WorldPointerGate(IHarmonyHost host, IPluginLog log)
    {
        _host = host;
        _log = log;
    }

    /// <summary>Install the patch if not yet done (idempotent; a missing type is retried on the next call, warned once).
    /// Called on engage so the detour is in place before the first frame the pointer reaches the world.</summary>
    public void EnsurePatched()
    {
        if (_touchPatched) return;
        try
        {
            _harmony ??= _host.Create("stickycursor");
            _touchPatched = PatchTouch(_harmony);
        }
        catch (Exception ex) { _log.Warning($"[PointerGate] patch install threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Arm (on) / disarm (off). Called on a state change only. <paramref name="clickSwallowed"/> (disarm only):
    /// the armed period ended a world click, so the line is logged once per swallowed click. The skip counter counts
    /// frames, not presses, so it can't tell a click from the pointer merely crossing the world.</summary>
    public void SetArmed(bool on, bool clickSwallowed = false)
    {
        if (on == _armed) return;
        if (on)
        {
            EnsurePatched();
            _touchSimSkipped = 0;
            _armed = true;
            return;
        }
        _armed = false;
        if (clickSwallowed)
            _log.Info($"[StickyCursor] swallowed world click (touch frames skipped: {_touchSimSkipped})");
    }

    public void Dispose() => SetArmed(false);

    private bool PatchTouch(Harmony harmony)
    {
        var type = StellarInterop.FindType(TouchTypeName);
        if (type is null)
        {
            if (!_touchWarned)
            {
                _touchWarned = true;
                _log.Warning($"[PointerGate] {TouchTypeName} not found — mouse touch simulation not gated (retried on next engage)");
            }
            return false;
        }
        try
        {
            // private void simulateTouch(float), unique by (name, arity) on this type. Not inlined into updateTouch
            // despite the shared (ICF) RVA with Update: the detour fired every armed frame in-game.
            var m = StellarInterop.FindMethod(type, "simulateTouch", 1);
            if (m is null || m.ReturnType != typeof(void)) { _log.Warning($"[PointerGate] {TouchTypeName}.simulateTouch(float) not found"); return true; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(WorldPointerGate), nameof(PrefixTouchSim)));
            _log.Info($"[PointerGate] {type.FullName}.simulateTouch patched");
        }
        catch (Exception ex) { _log.Warning($"[PointerGate] {TouchTypeName}.simulateTouch patch failed: {ex.Message}"); }
        return true;   // a failure is logged; don't retry a method the game has but Harmony rejected
    }

    // Skip = no mouse touch is created/updated this frame. Armed only before a press (the caller arms while the pointer
    // hovers the world, holds until all buttons are up), so no touch is left mid-life when the skip starts.
    private static bool PrefixTouchSim()
    {
        if (!_armed) return true;
        _touchSimSkipped++;
        return false;
    }
}
