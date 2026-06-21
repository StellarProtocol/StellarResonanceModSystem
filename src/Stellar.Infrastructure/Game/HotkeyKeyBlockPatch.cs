using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Harmony patches that suppress flagged key+modifier combos from reaching the game via Rewired.
/// Only the exact binding (primary key AND modifiers) is blocked — pressing F1 alone will not
/// block it when the registered binding is Ctrl+F1.
/// </summary>
internal sealed class HotkeyKeyBlockPatch
{
    // Stores (primaryKey, modifierMask) pairs so modifier-qualified bindings are matched exactly.
    private static readonly HashSet<(int key, int mods)> _blocked = new();
    private static bool _captureMode;
    private Harmony? _harmony;

    public void Install(string harmonyId, Action<string> log)
    {
        _harmony = new Harmony(harmonyId + ".keyblock");
        int patched = 0;

        // Only patch Rewired.Keyboard — the game reads input through Rewired, so this
        // blocks the game without affecting the framework (which polls UnityEngine.Input
        // directly via UnityInputGateway). Hotkey callbacks still fire normally.
        var kbType = FindType("Rewired.Keyboard");
        var kcType = FindType("UnityEngine.KeyCode") ?? typeof(int);
        if (kbType != null)
        {
            foreach (var name in new[] { "GetKey", "GetKeyDown", "GetKeyUp" })
            {
                try
                {
                    var m = kbType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, new[] { kcType }, null);
                    if (m == null) continue;
                    _harmony.Patch(m, prefix: new HarmonyMethod(typeof(HotkeyKeyBlockPatch), nameof(PrefixBlock)));
                    patched++;
                }
                catch (Exception ex) { log($"[KeyBlock] Rewired.Keyboard.{name} patch failed: {ex.Message}"); }
            }
        }

        log($"[KeyBlock] installed — {patched} Rewired.Keyboard methods patched");
    }

    public void Uninstall() { _harmony?.UnpatchSelf(); _harmony = null; _blocked.Clear(); _captureMode = false; }

    public void Update(IEnumerable<KeyBinding> bindings)
    {
        _blocked.Clear();
        foreach (var b in bindings) _blocked.Add(((int)b.Key, (int)b.Modifiers));
    }

    public void SetCaptureMode(bool active) => _captureMode = active;

    // __0 = positional injection — avoids name mismatch between "key" (UnityEngine.Input) and "keyCode" (Rewired.Keyboard)
    private static bool PrefixBlock(int __0, ref bool __result)
    {
        if (_captureMode) { __result = false; return false; }
        if (_blocked.Count > 0 && _blocked.Contains((__0, (int)GetCurrentModifiers())))
        {
            __result = false;
            return false;
        }
        return true;
    }

    private static ModifierKeys GetCurrentModifiers()
    {
        try
        {
            var m = ModifierKeys.None;
            if (Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift))   m |= ModifierKeys.Shift;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= ModifierKeys.Ctrl;
            if (Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt))     m |= ModifierKeys.Alt;
            return m;
        }
        catch { return ModifierKeys.None; }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = asm.GetType(fullName); if (t != null) return t; } catch { }
        }
        return null;
    }
}
