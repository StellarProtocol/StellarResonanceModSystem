using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Second half of the sticky cursor's world-click swallow, next to <see cref="RewiredMouseGate"/>. Disabling Rewired's
/// mouse stops attacks and targeting actions, but NOT world model-picking (clicking another player still opened the
/// avatar badge in-game, fw <c>637f0df</c>): picking reads the game's own pointer layer, <c>ZMouseManager.PlayerMousePointer</c>
/// → <c>PlayerMousePointerAdpter</c> → (PC) <c>Panda.ZInput.DefaultMousePointer</c> → <c>ZTouchManager</c> → <c>ZTouch</c>
/// → <c>PlayerInputController.handleTouch</c> → <c>Panda.ZInput.ZInputSelect.OnSelectTouch(ZTouch)</c> → Lua
/// <c>ON_IDCARD_INFO</c> (chain inferred from dump.cs). Belt and braces, both armed together:
/// <list type="bullet">
/// <item>POSTFIX on <c>DefaultMousePointer.GetMouseButtonDown/GetMouseButtonUp/GetMouseButton(int)</c>: every button reads
/// as not pressed, so no touch is ever produced.</item>
/// <item>PREFIX on <c>ZInputSelect.OnSelectTouch(ZTouch)</c>: skipped, in case a touch still gets through another path.</item>
/// </list>
/// <para>⚠️ uGUI (<c>ZCustomMouseInput</c>) reads the same pointer source, so this must only be armed while the pointer is
/// over the WORLD, never over game UI or a Stellar window. The caller guarantees that: it arms exactly when the Rewired
/// gate is armed (sticky + pointer over the world, held until the buttons are up).</para>
/// <para>Patched lazily on first need (engage or first arm), never unpatched at runtime (these are per-frame methods; a
/// runtime unpatch risks the native trampoline crash). The framework's <see cref="IHarmonyHost"/> unpatches on teardown.
/// Unarmed, the patch bodies exit on one static bool. Both targets take only an <c>int</c> / a class, no in/ref struct.</para>
/// </summary>
internal sealed class WorldPointerGate : IDisposable
{
    private const string PointerTypeName = "Panda.ZInput.DefaultMousePointer";
    private const string SelectTypeName = "Panda.ZInput.ZInputSelect";
    private static readonly string[] PointerMethods = { "GetMouseButtonDown", "GetMouseButtonUp", "GetMouseButton" };

    // Read by the patch bodies (main thread). Counters are reset each arm, reported on disarm.
    private static bool _armed;
    private static int _downSwallowed, _upSwallowed, _heldSwallowed, _selectSkipped;

    private readonly IHarmonyHost _host;
    private readonly IPluginLog _log;
    private Harmony? _harmony;
    private bool _pointerPatched, _selectPatched;
    private bool _pointerWarned, _selectWarned;

    public WorldPointerGate(IHarmonyHost host, IPluginLog log)
    {
        _host = host;
        _log = log;
    }

    /// <summary>Install the patches if not yet done (idempotent; a missing type is retried on the next call, warned once).
    /// Called on engage so the detours are in place before the first frame the pointer reaches the world.</summary>
    public void EnsurePatched()
    {
        if (_pointerPatched && _selectPatched) return;
        try
        {
            _harmony ??= _host.Create("stickycursor");
            if (!_pointerPatched) _pointerPatched = PatchPointer(_harmony);
            if (!_selectPatched) _selectPatched = PatchSelect(_harmony);
        }
        catch (Exception ex) { _log.Warning($"[PointerGate] patch install threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Arm (on) / disarm (off). Called on a state change only. Disarming logs what was swallowed, so the in-game
    /// test tells us which path actually fired.</summary>
    public void SetArmed(bool on)
    {
        if (on == _armed) return;
        if (on)
        {
            EnsurePatched();
            _downSwallowed = _upSwallowed = _heldSwallowed = _selectSkipped = 0;
            _armed = true;
            return;
        }
        _armed = false;
        if (_downSwallowed + _upSwallowed + _heldSwallowed + _selectSkipped > 0)
            _log.Info($"[StickyCursor] swallowed world click: pointerDown={_downSwallowed} pointerUp={_upSwallowed} pointerHeld={_heldSwallowed} selectTouch={_selectSkipped}");
    }

    public void Dispose() => SetArmed(false);

    private bool PatchPointer(Harmony harmony)
    {
        var type = StellarInterop.FindType(PointerTypeName);
        if (type is null) { WarnOnce(ref _pointerWarned, $"{PointerTypeName} not found — world pointer not gated (retried on next engage)"); return false; }
        var patched = 0;
        foreach (var name in PointerMethods)
        {
            try
            {
                // Unique by (name, arity) on this type (dump.cs:153233-153239): bool X(int button).
                var m = StellarInterop.FindMethod(type, name, 1);
                if (m is null || m.ReturnType != typeof(bool)) { _log.Warning($"[PointerGate] {PointerTypeName}.{name}(int) not found"); continue; }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(WorldPointerGate), PostfixName(name)));
                patched++;
            }
            catch (Exception ex) { _log.Warning($"[PointerGate] {PointerTypeName}.{name} patch failed: {ex.Message}"); }
        }
        _log.Info($"[PointerGate] {type.FullName}: {patched}/{PointerMethods.Length} button reads patched");
        return true;   // a per-method failure is logged; retrying would only double-patch the ones that took
    }

    private bool PatchSelect(Harmony harmony)
    {
        var type = StellarInterop.FindType(SelectTypeName);
        if (type is null) { WarnOnce(ref _selectWarned, $"{SelectTypeName} not found — select-touch not gated (retried on next engage)"); return false; }
        try
        {
            var m = StellarInterop.FindMethod(type, "OnSelectTouch", 1);   // void OnSelectTouch(ZTouch) — ZTouch is a class
            if (m is null) { _log.Warning($"[PointerGate] {SelectTypeName}.OnSelectTouch(ZTouch) not found"); return true; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(WorldPointerGate), nameof(PrefixSelectTouch)));
            _log.Info($"[PointerGate] {type.FullName}.OnSelectTouch({m.GetParameters()[0].ParameterType.FullName}) patched");
        }
        catch (Exception ex) { _log.Warning($"[PointerGate] {SelectTypeName}.OnSelectTouch patch failed: {ex.Message}"); }
        return true;
    }

    private void WarnOnce(ref bool warned, string message)
    {
        if (warned) return;
        warned = true;
        _log.Warning($"[PointerGate] {message}");
    }

    private static string PostfixName(string method) => method switch
    {
        "GetMouseButtonDown" => nameof(PostfixDown),
        "GetMouseButtonUp" => nameof(PostfixUp),
        _ => nameof(PostfixHeld),
    };

    // Count only reads that were actually true, so the diagnostic says "the game saw a press here and we hid it".
    private static void PostfixDown(ref bool __result) { if (_armed && __result) { __result = false; _downSwallowed++; } }
    private static void PostfixUp(ref bool __result) { if (_armed && __result) { __result = false; _upSwallowed++; } }
    private static void PostfixHeld(ref bool __result) { if (_armed && __result) { __result = false; _heldSwallowed++; } }

    private static bool PrefixSelectTouch()
    {
        if (!_armed) return true;
        _selectSkipped++;
        return false;
    }
}
