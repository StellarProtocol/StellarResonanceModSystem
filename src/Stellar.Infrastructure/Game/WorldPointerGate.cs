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
/// <item>PREFIX (skip) on <c>Panda.ZInput.ZTouchManager.simulateTouch(float)</c> + <c>getMouseButton/getMouseButtonUp(int,float,int)</c>
/// (all void, dump.cs:156019-156028): fw <c>9cd870d</c> in-game, the two above fired (pointerDown=3) / never fired
/// (selectTouch=0) and the badge STILL opened, so the mouse→<c>ZTouch</c> conversion evidently reads raw input itself.
/// Skipping it means no mouse touch is generated while armed.</item>
/// <item>Lua backstop for the avatar badge only: the <c>mainui</c> VM's <c>RefreshIdCard</c> (called by the bridge's
/// <c>ON_IDCARD_INFO</c>, which looks the field up on every call, <c>lua_event_bridge.lua:113-117</c>) is wrapped once to
/// return early while <c>_G.__stellarStickySwallow == true</c>. The global is set/cleared on arm/disarm transitions only.</item>
/// </list>
/// <para>⚠️ uGUI (<c>ZCustomMouseInput</c>) reads the same pointer source, so this must only be armed while the pointer is
/// over the WORLD, never over game UI or a Stellar window. The caller guarantees that: it arms exactly when the Rewired
/// gate is armed (sticky + pointer over the world, held until the buttons are up).</para>
/// <para>Patched lazily on first need (engage or first arm), never unpatched at runtime (these are per-frame methods; a
/// runtime unpatch risks the native trampoline crash). The framework's <see cref="IHarmonyHost"/> unpatches on teardown.
/// Unarmed, the patch bodies exit on one static bool. Every target takes only <c>int</c>/<c>float</c>/a class, no in/ref struct.</para>
/// </summary>
internal sealed class WorldPointerGate : IDisposable
{
    private const string PointerTypeName = "Panda.ZInput.DefaultMousePointer";
    private const string SelectTypeName = "Panda.ZInput.ZInputSelect";
    private const string TouchTypeName = "Panda.ZInput.ZTouchManager";
    private static readonly string[] PointerMethods = { "GetMouseButtonDown", "GetMouseButtonUp", "GetMouseButton" };

    // Arm: wrap RefreshIdCard once (idempotent via a marker on the VM table, so re-running each arm also covers a VM that
    // wasn't loaded yet last time), zero the counter, raise the flag. The wrapper is a plain dot-call function like the
    // original (mainui_vm.lua:151, `MainUiVM.RefreshIdCard = function(charIdList)`). __stellarIdCardGuard reports whether
    // the wrap is in place (read back once for the install log). pcall per the ILua contract.
    private const string ArmChunk =
        "pcall(function() rawset(_G,'__stellarIdCardBlocked',0) rawset(_G,'__stellarStickySwallow',true) " +
        "local vm = Z and Z.VMMgr and Z.VMMgr.GetVM and Z.VMMgr.GetVM('mainui') " +
        "if type(vm) == 'table' and not vm.__stellarIdCardWrapped and type(vm.RefreshIdCard) == 'function' then " +
        "local orig = vm.RefreshIdCard " +
        "vm.RefreshIdCard = function(...) if rawget(_G,'__stellarStickySwallow') == true then " +
        "rawset(_G,'__stellarIdCardBlocked',(rawget(_G,'__stellarIdCardBlocked') or 0) + 1) return end return orig(...) end " +
        "vm.__stellarIdCardWrapped = true end " +
        "rawset(_G,'__stellarIdCardGuard',type(vm) == 'table' and vm.__stellarIdCardWrapped == true) end)";
    private const string DisarmChunk = "pcall(function() rawset(_G,'__stellarStickySwallow',false) end)";

    // Read by the patch bodies (main thread). Counters are reset each arm, reported on disarm.
    private static bool _armed;
    private static int _downSwallowed, _upSwallowed, _heldSwallowed, _selectSkipped;
    private static int _touchSimSkipped, _touchDownSkipped, _touchUpSkipped;

    private readonly IHarmonyHost _host;
    private readonly ILua _lua;
    private readonly IPluginLog _log;
    private Harmony? _harmony;
    private bool _pointerPatched, _selectPatched, _touchPatched;
    private bool _pointerWarned, _selectWarned, _touchWarned;
    private bool _idCardGuardLogged, _luaArmed;

    public WorldPointerGate(IHarmonyHost host, ILua lua, IPluginLog log)
    {
        _host = host;
        _lua = lua;
        _log = log;
    }

    /// <summary>Install the patches if not yet done (idempotent; a missing type is retried on the next call, warned once).
    /// Called on engage so the detours are in place before the first frame the pointer reaches the world.</summary>
    public void EnsurePatched()
    {
        if (_pointerPatched && _selectPatched && _touchPatched) return;
        try
        {
            _harmony ??= _host.Create("stickycursor");
            if (!_pointerPatched) _pointerPatched = PatchPointer(_harmony);
            if (!_selectPatched) _selectPatched = PatchSelect(_harmony);
            if (!_touchPatched) _touchPatched = PatchTouch(_harmony);
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
            _touchSimSkipped = _touchDownSkipped = _touchUpSkipped = 0;
            _armed = true;
            ArmLua();
            return;
        }
        _armed = false;
        var idCard = DisarmLua();
        // touch* count every skipped CALL (a per-frame tick while armed), not presses, so they don't trigger the line on
        // their own: it fires when a real press was seen (pointer reads) or something was actually blocked.
        if (_downSwallowed + _upSwallowed + _heldSwallowed + _selectSkipped + idCard > 0)
            _log.Info($"[StickyCursor] swallowed world click: pointerDown={_downSwallowed} pointerUp={_upSwallowed} pointerHeld={_heldSwallowed} selectTouch={_selectSkipped} touchSim={_touchSimSkipped} touchDown={_touchDownSkipped} touchUp={_touchUpSkipped} idCard={idCard}");
    }

    // Transitions only (SetArmed early-outs on no change), never per frame. If Lua isn't ready the flag simply isn't
    // raised this time; the wrap is (re)attempted on every arm, so a mainui VM that wasn't loaded yet is picked up later.
    private void ArmLua()
    {
        if (!_lua.Ready) return;
        try
        {
            _lua.DoString(ArmChunk);
            _luaArmed = true;
            if (!_idCardGuardLogged && _lua.TryReadGlobalBool("__stellarIdCardGuard", out var ok) && ok)
            {
                _idCardGuardLogged = true;
                _log.Info("[PointerGate] mainui RefreshIdCard guard installed");
            }
        }
        catch (Exception ex) { _log.Warning($"[PointerGate] idCard guard arm failed: {ex.Message}"); }
    }

    /// <summary>Clear the Lua flag (only if we raised it) and return how many RefreshIdCard calls were blocked.</summary>
    private int DisarmLua()
    {
        if (!_luaArmed) return 0;
        _luaArmed = false;
        try
        {
            _lua.DoString(DisarmChunk);
            return _lua.TryReadGlobalNumber("__stellarIdCardBlocked", out var n) ? (int)n : 0;
        }
        catch (Exception ex) { _log.Warning($"[PointerGate] idCard guard disarm failed: {ex.Message}"); return 0; }
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

    private bool PatchTouch(Harmony harmony)
    {
        var type = StellarInterop.FindType(TouchTypeName);
        if (type is null) { WarnOnce(ref _touchWarned, $"{TouchTypeName} not found — mouse touch simulation not gated (retried on next engage)"); return false; }
        // All private void, unique by (name, arity) on this type (dump.cs:156022/156025/156028); params are float / int,float,int.
        // ⚠️ simulateTouch is called from the private updateTouch (whose RVA is shared with Update, ICF) and could be inlined
        // there by IL2CPP; the getMouseButton* skips are the fallback if the simulateTouch detour never fires.
        (string Name, int Arity, string Prefix)[] targets =
        {
            ("simulateTouch", 1, nameof(PrefixTouchSim)),
            ("getMouseButton", 3, nameof(PrefixTouchDown)),
            ("getMouseButtonUp", 3, nameof(PrefixTouchUp)),
        };
        var patched = 0;
        foreach (var (name, arity, prefix) in targets)
        {
            try
            {
                var m = StellarInterop.FindMethod(type, name, arity);
                if (m is null || m.ReturnType != typeof(void)) { _log.Warning($"[PointerGate] {TouchTypeName}.{name}/{arity} not found"); continue; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(WorldPointerGate), prefix));
                patched++;
            }
            catch (Exception ex) { _log.Warning($"[PointerGate] {TouchTypeName}.{name} patch failed: {ex.Message}"); }
        }
        _log.Info($"[PointerGate] {type.FullName}: {patched}/{targets.Length} touch-simulation methods patched");
        return true;   // same as PatchPointer: don't retry (would double-patch the ones that took)
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

    // Skip = no mouse touch is created/updated this frame. Armed only before a press (the caller arms while the pointer
    // hovers the world, holds until all buttons are up), so no touch is left mid-life when the skip starts.
    private static bool PrefixTouchSim()
    {
        if (!_armed) return true;
        _touchSimSkipped++;
        return false;
    }

    private static bool PrefixTouchDown()
    {
        if (!_armed) return true;
        _touchDownSkipped++;
        return false;
    }

    private static bool PrefixTouchUp()
    {
        if (!_armed) return true;
        _touchUpSkipped++;
        return false;
    }
}
