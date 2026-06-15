using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Suppresses the game's keybinds while a Stellar text field is focused, using the game's OWN mechanisms via
/// Il2CppInterop reflection (Stellar has no compile-time Panda reference — types resolve at runtime through
/// IGameTypeRegistry, the PandaPlayerStateProbe pattern). Three co-mechanisms, applied together, available if
/// ANY resolves:
/// (1) PRIMARY — disable Rewired's keyboard controller (<c>ReInput.controllers.Keyboard.enabled=false</c>),
/// the input backend every game layer reads through, so movement + skills + UI hotkeys all stop at once;
/// (2) <c>PlayerInputController.IgnoreKeyboard</c>, the game's own chat-focus flag;
/// (3) <c>ZIgnoreMgr.SetInputIgnore</c> gameplay-action mask (proven to stop movement).
/// Policy-correct: triggers the game's own gates, never short-circuits input. SetSuppressed is idempotent +
/// transition-guarded; resolution is lazy + cached; any failure logs once and disables that path (never throws
/// into the per-frame loop). Dispose() releases both (mod isolation — never leave the keyboard ignored).
/// </summary>
internal sealed class KeyboardInputGate
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const long CustomOperateId = 10002L;

    // While a Stellar text field is focused we mask ALL gameplay EInputMask actions (movement, skills,
    // abilities, emotes, parkour, wheels, interact, …) — a hand-picked subset leaked skills/emotes/parkour.
    // These substrings mark bits to LEAVE UNMASKED so the user can still mouse-look + click away to
    // defocus: enum sentinels + camera/pointer/cursor/UI/confirm-cancel. Matched case-insensitively.
    private static readonly string[] MaskExcludeSubstrings =
        { "none", "all", "max", "count", "invalid", "default",
          "camera", "view", "mouse", "zoom", "rotat", "cursor", "point",
          "ui", "click", "select", "confirm", "cancel" };

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;

    private bool _available;
    private bool _suppressed;

    private object? _instance;           // ZIgnoreMgr.Instance
    private MethodInfo? _setInputIgnore;  // SetInputIgnore(EInputMask, bool, EIgnoreMaskSource[, long])
    private object[] _maskValues = Array.Empty<object>();  // resolved EInputMask values (by name)
    private object? _source;              // EIgnoreMaskSource.EUIView
    private bool _hasLongParam;
    private bool _maskAvailable;          // ZIgnoreMgr gameplay-mask path resolved

    // PRIMARY for complete coverage: the game's own "a UI text field is focused → ignore keyboard" flag
    // (what its chat box uses) — covers movement + skills + UI hotkeys (inventory/wheel/modules) that the
    // gameplay mask can't reach. Exposed by the interop as a field OR a property; resolve either.
    private object? _pcInstance;          // PlayerInputController.Instance
    private bool _pcAvailable;            // any PlayerInputController focus member resolved
    private FieldInfo? _ignoreKbField;    // PlayerInputController.IgnoreKeyboard (if a field)
    private PropertyInfo? _ignoreKbProp;  // PlayerInputController.IgnoreKeyboard (if a property)
    // The game's own "a UI text field is focused" trackers — its OpenChat guard reads these (chat opens on
    // Enter only when NO field is focused). Setting them makes Enter NOT open chat while our field is focused.
    private FieldInfo? _focusNameField;   // curFocusInputTextName (string)
    private FieldInfo? _focusUiField;     // isFocusUI_ (bool)

    // PRIMARY for COMPLETE coverage: disable Rewired's keyboard controller (the input backend EVERY game
    // layer reads through), so movement + skills + UI hotkeys all stop at once, while mouse-look/click and
    // the uGUI field's own typing (Unity Input.inputString, not Rewired) keep working. ReInput.controllers
    // (static) → .Keyboard → .enabled. Live objects are re-fetched per transition (cheap; not per-frame).
    private bool _rewiredAvailable;
    private PropertyInfo? _reinputControllersProp;  // static Rewired.ReInput.controllers
    private PropertyInfo? _kbProp;                   // ControllerHelper.Keyboard
    private PropertyInfo? _kbEnabledProp;            // Controller.enabled (settable bool)

    public KeyboardInputGate(IGameTypeRegistry types, IPluginLog log) { _types = types; _log = log; }

    /// <summary>Mask (on=true) / unmask (on=false) the gameplay keybind set while a Stellar field is focused.
    /// Idempotent + safe to call every frame; no-op if the game API couldn't be resolved.</summary>
    public void SetSuppressed(bool on)
    {
        // Transition guard FIRST — before EnsureResolved. The window/HUD tick calls this every frame, so at
        // the TITLE screen it fires SetSuppressed(false) constantly; if that triggered resolution, it would
        // run before the game's input singletons (Rewired / PlayerInputController / ZIgnoreMgr) exist, fail,
        // and the gate would cache itself dead → keyboard leaks in-world (the spike dodged this by only
        // creating the gate in-world). With the guard first, resolution is attempted only on a real
        // false→true transition — i.e. the first time a field is focused, which is in-world where the
        // singletons are live.
        if (on == _suppressed) return;
        if (!EnsureResolved()) return;   // not resolvable yet — leave _suppressed unflipped so we retry next focus
        _suppressed = on;
        if (_rewiredAvailable) SetRewiredKeyboardEnabled(!on);   // primary complete block
        if (_pcAvailable) SetPlayerFocusState(on);                // IgnoreKeyboard + focus-text trackers (gates Enter→chat)
        if (_maskAvailable)
        {
            try
            {
                for (var i = 0; i < _maskValues.Length; i++)
                    _setInputIgnore!.Invoke(_instance, BuildArgs(_maskValues[i], on));
            }
            catch (Exception ex)
            {
                _log.Warning($"[KeyboardGate] SetInputIgnore threw, disabling mask path: {ex.GetType().Name}: {ex.Message}");
                _maskAvailable = false;
            }
        }
    }

    private void SetPlayerFocusState(bool on)
    {
        try
        {
            if (_ignoreKbField is not null) _ignoreKbField.SetValue(_pcInstance, on);
            else _ignoreKbProp?.SetValue(_pcInstance, on);
            _focusUiField?.SetValue(_pcInstance, on);
            _focusNameField?.SetValue(_pcInstance, on ? "StellarField" : string.Empty);
        }
        catch (Exception ex)
        {
            _log.Warning($"[KeyboardGate] PlayerInputController focus set threw: {ex.GetType().Name}: {ex.Message}");
            _pcAvailable = false;
        }
    }

    // Re-fetch the live controllers/Keyboard objects each transition, then set enabled.
    private void SetRewiredKeyboardEnabled(bool enabled)
    {
        try
        {
            var helper = _reinputControllersProp!.GetValue(null);
            var kb = helper is null ? null : _kbProp!.GetValue(helper);
            if (kb is not null) _kbEnabledProp!.SetValue(kb, enabled);
        }
        catch (Exception ex)
        {
            _log.Warning($"[KeyboardGate] Rewired keyboard set threw: {ex.GetType().Name}: {ex.Message}");
            _rewiredAvailable = false;
        }
    }

    /// <summary>Clear any masks we set (mod isolation) — call on teardown.</summary>
    public void Dispose()
    {
        UnregisterTeardownHooks();
        if (_suppressed) SetSuppressed(false);
    }

    // ---- crash/exit hardening: never leave the keyboard (or Alt) disabled ----

    private bool _teardownHooked;

    private void RegisterTeardownHooks()
    {
        if (_teardownHooked) return;
        _teardownHooked = true;
        AppDomain.CurrentDomain.ProcessExit += OnTeardown;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void UnregisterTeardownHooks()
    {
        if (!_teardownHooked) return;
        _teardownHooked = false;
        AppDomain.CurrentDomain.ProcessExit -= OnTeardown;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }

    private void OnTeardown(object? sender, EventArgs e) => ForceRestore();
    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e) => ForceRestore();

    // Best-effort re-enable — fires on a non-main thread (UnhandledException/ProcessExit) where the
    // Rewired/Il2CppInterop calls aren't thread-safe. Intentionally NOT synchronized: we only ever
    // re-enable (never disable), so the worst a race yields is a harmless redundant call or a swallowed
    // exception. Must never throw out of a teardown handler.
    private void ForceRestore()
    {
        try { if (_suppressed) SetSuppressed(false); }
        catch { /* swallow: teardown path, nothing to recover to */ }
    }

    private object[] BuildArgs(object maskValue, bool on)
        => _hasLongParam
            ? new object[] { maskValue, on, _source!, CustomOperateId }
            : new object[] { maskValue, on, _source! };

    private bool EnsureResolved()
    {
        if (_available) return true;   // resolved once → cached

        _rewiredAvailable = TryResolveRewired();      // primary — complete keyboard block
        _pcAvailable = TryResolvePlayerFocus();        // IgnoreKeyboard + focus trackers (gates Enter->chat)
        _maskAvailable = TryResolveMaskPath();         // gameplay-action mask (movement/skills)

        _available = _rewiredAvailable || _pcAvailable || _maskAvailable;
        // Do NOT permanently disable on a miss — a focus transition can land a frame before a singleton is
        // live (scene/login boundary); just report not-ready and retry on the next focus (SetSuppressed only
        // reaches here on a real transition, so this isn't a per-frame retry).
        if (!_available) return false;
        RegisterTeardownHooks();   // never leave the keyboard disabled on crash/exit
        _log.Info($"[KeyboardGate] resolved: rewiredKeyboard={_rewiredAvailable} playerFocus={_pcAvailable} maskPath={_maskAvailable}");
        return true;
    }

    // Rewired keyboard controller: ReInput.controllers (static) → .Keyboard → .enabled. Disabling it stops
    // ALL keyboard-driven game actions (every layer reads through Rewired) while leaving mouse + the uGUI
    // field's typing (Unity Input.inputString) intact. Caches the property chain; live objects re-fetched on use.
    private bool TryResolveRewired()
    {
        var reInput = _types.FindType("Rewired.ReInput") ?? FindByShortName("ReInput");
        if (reInput is null) { _log.Info("[KeyboardGate] Rewired.ReInput not found"); return false; }
        _reinputControllersProp = reInput.GetProperty("controllers", AnyStatic);
        var helper = _reinputControllersProp?.GetValue(null);
        if (helper is null) { _log.Info("[KeyboardGate] ReInput.controllers null"); return false; }
        _kbProp = helper.GetType().GetProperty("Keyboard", AnyInstance);
        var kb = _kbProp?.GetValue(helper);
        if (kb is null) { _log.Info("[KeyboardGate] controllers.Keyboard null"); return false; }
        _kbEnabledProp = kb.GetType().GetProperty("enabled", AnyInstance);
        if (_kbEnabledProp is null || !_kbEnabledProp.CanWrite) { _log.Info("[KeyboardGate] Keyboard.enabled not settable"); return false; }
        _log.Info("[KeyboardGate] Rewired keyboard controller resolved");
        return true;
    }

    // The game's own chat-focus flag — covers movement + skills + UI hotkeys (the complete block).
    private bool TryResolvePlayerFocus()
    {
        var pcType = _types.FindType("Panda.ZInput.PlayerInputController") ?? FindByShortName("PlayerInputController");
        if (pcType is null) { _log.Info("[KeyboardGate] PlayerInputController not found"); return false; }
        _pcInstance = FindSingletonInstanceProperty(pcType)?.GetValue(null);
        if (_pcInstance is null) { _log.Info("[KeyboardGate] PlayerInputController.Instance null"); return false; }
        _ignoreKbField = pcType.GetField("IgnoreKeyboard", AnyInstance);
        _ignoreKbProp = _ignoreKbField is null ? pcType.GetProperty("IgnoreKeyboard", AnyInstance) : null;
        if (_ignoreKbProp is { CanWrite: false }) _ignoreKbProp = null;
        _focusNameField = pcType.GetField("curFocusInputTextName", AnyInstance);
        _focusUiField = pcType.GetField("isFocusUI_", AnyInstance);
        var any = _ignoreKbField is not null || _ignoreKbProp is not null || _focusNameField is not null || _focusUiField is not null;
        if (!any) { _log.Info("[KeyboardGate] no settable PlayerInputController focus members"); return false; }
        _log.Info($"[KeyboardGate] PlayerInputController: ignoreKb={_ignoreKbField is not null || _ignoreKbProp is not null} focusName={_focusNameField is not null} focusUi={_focusUiField is not null}");
        return true;
    }

    // ZIgnoreMgr gameplay-action mask (movement/skills) — proven to suppress movement in-world.
    private bool TryResolveMaskPath()
    {
        var zIgnoreType = _types.FindType("Panda.ZGame.ZIgnoreMgr") ?? FindByShortName("ZIgnoreMgr");
        if (zIgnoreType is null) return false;
        _instance = FindSingletonInstanceProperty(zIgnoreType)?.GetValue(null);
        if (_instance is null) return false;
        var maskType = _types.FindType("Panda.ZGame.EInputMask") ?? FindByShortName("EInputMask");
        if (maskType is null || !maskType.IsEnum) return false;
        _maskValues = ResolveMaskValues(maskType);
        if (_maskValues.Length == 0) return false;
        var sourceType = _types.FindType("Panda.ZGame.EIgnoreMaskSource") ?? FindByShortName("EIgnoreMaskSource");
        _source = sourceType is { IsEnum: true } ? TryParseEnum(sourceType, "EUIView") : null;
        if (_source is null) return false;
        _setInputIgnore = ResolveSetInputIgnore(zIgnoreType, maskType);
        if (_setInputIgnore is null) return false;
        var sig = string.Join(",", Array.ConvertAll(_setInputIgnore.GetParameters(), p => p.ParameterType.Name));
        _log.Info($"[KeyboardGate] mask path: {_setInputIgnore.Name}({sig}) source=EUIView masks={_maskValues.Length}");
        return true;
    }

    private object[] ResolveMaskValues(Type maskType)
    {
        var names = Enum.GetNames(maskType);
        _log.Info($"[KeyboardGate] EInputMask has {names.Length} values: {string.Join(",", names)}");
        var list = new List<object>(names.Length);
        var masked = new List<string>();
        foreach (var name in names)
        {
            if (IsExcludedMask(name)) continue;
            var v = TryParseEnum(maskType, name);
            if (v is not null) { list.Add(v); masked.Add(name); }
        }
        _log.Info($"[KeyboardGate] masking {masked.Count}: {string.Join(",", masked)}");
        return list.ToArray();
    }

    // True for EInputMask names we must NOT mask (sentinels + camera/pointer/UI) so the user can still
    // mouse-look and click away to defocus the field while everything else is suppressed.
    private static bool IsExcludedMask(string name)
    {
        var n = name.ToLowerInvariant();
        foreach (var ex in MaskExcludeSubstrings)
            if (n.Contains(ex)) return true;
        return false;
    }

    // First instance overload whose params are (EInputMask enum, bool, <source>, [long]).
    private MethodInfo? ResolveSetInputIgnore(Type zIgnoreType, Type maskType)
    {
        foreach (var m in zIgnoreType.GetMethods(AnyInstance))
        {
            if (m.Name != "SetInputIgnore") continue;
            var ps = m.GetParameters();
            if (ps.Length < 3 || ps[0].ParameterType != maskType || ps[1].ParameterType != typeof(bool)) continue;
            _hasLongParam = ps.Length >= 4 && ps[3].ParameterType == typeof(long);
            return m;
        }
        return null;
    }

    // ---- reflection idioms mirrored from PandaPlayerStateProbe.Bootstrap ----

    private static PropertyInfo? FindSingletonInstanceProperty(Type tMgr)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? singleton;
            try { singleton = assembly.GetType("ZUtil.ZSingleton`1", throwOnError: false); }
            catch { continue; }
            if (singleton is null) continue;
            try
            {
                var prop = singleton.MakeGenericType(tMgr).GetProperty("Instance", AnyStatic);
                if (prop is not null) return prop;
            }
            catch { /* try next assembly */ }
        }
        return null;
    }

    private static object? TryParseEnum(Type enumType, string name)
    {
        try { if (Enum.IsDefined(enumType, name)) return Enum.Parse(enumType, name); }
        catch { /* ignore */ }
        return null;
    }

    private static Type? FindByShortName(string shortName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }
            foreach (var t in types) if (t.Name == shortName) return t;
        }
        return null;
    }
}
