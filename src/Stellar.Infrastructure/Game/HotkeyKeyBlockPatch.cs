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
///
/// <para><b>Perf contract (P2 of the 2026-07-25 jitter work):</b> Rewired calls the patched
/// <c>Keyboard.GetKey*</c> methods ~8–25k times/sec (measured via <c>hook:rewired</c>), so the
/// prefixes exist ONLY while they can do something — a non-empty blocked set or capture mode.
/// With no blocks configured (the default), no patch is installed and the game's input path has
/// zero framework detours. The prefix also pre-filters on the primary key before reading
/// modifiers, so the 6 <c>Input.GetKey</c> interop reads only run for queries about a key that
/// is actually part of a blocked binding.</para>
/// </summary>
internal sealed class HotkeyKeyBlockPatch
{
    // Stores (primaryKey, modifierMask) pairs so modifier-qualified bindings are matched exactly,
    // plus the primary-key set for the cheap pre-filter in the hot prefix.
    private static readonly HashSet<(int key, int mods)> _blocked = new();
    private static readonly HashSet<int> _blockedPrimaries = new();
    private static bool _captureMode;

    private Harmony? _harmony;
    private string? _harmonyId;
    private Action<string>? _log;
    private bool _patched;

    /// <summary>Capture the patch identity/log sink. Does NOT patch — the Harmony prefixes are
    /// installed lazily by <see cref="ReconcilePatched"/> when a block/capture first needs them.</summary>
    public void Install(string harmonyId, Action<string> log)
    {
        _harmonyId = harmonyId;
        _log = log;
        log("[KeyBlock] armed — Rewired prefixes install on first blocked binding / capture (perf: no detours while unused)");
    }

    public void Uninstall()
    {
        RemovePatches();
        _blocked.Clear();
        _blockedPrimaries.Clear();
        _captureMode = false;
    }

    public void Update(IEnumerable<KeyBinding> bindings)
    {
        _blocked.Clear();
        _blockedPrimaries.Clear();
        foreach (var b in bindings)
        {
            _blocked.Add(((int)b.Key, (int)b.Modifiers));
            _blockedPrimaries.Add((int)b.Key);
        }
        ReconcilePatched();
    }

    public void SetCaptureMode(bool active)
    {
        _captureMode = active;
        ReconcilePatched();
    }

    // Install/remove the Rewired prefixes to match need. Runs on the main thread (HotkeyService
    // tick / capture UI); patching costs ~ms and only happens on a block-set or capture-mode
    // transition, never per-frame.
    private void ReconcilePatched()
    {
        var want = _captureMode || _blocked.Count > 0;
        if (want == _patched) return;
        if (want) InstallPatches();
        else RemovePatches();
    }

    private void InstallPatches()
    {
        if (_harmonyId is null || _log is null) return;   // Install() not called yet
        _harmony = new Harmony(_harmonyId + ".keyblock");
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
                catch (Exception ex) { _log($"[KeyBlock] Rewired.Keyboard.{name} patch failed: {ex.Message}"); }
            }
        }

        _patched = true;
        _log($"[KeyBlock] installed — {patched} Rewired.Keyboard methods patched (blocked={_blocked.Count} capture={_captureMode})");
    }

    private void RemovePatches()
    {
        if (!_patched && _harmony is null) return;
        try { _harmony?.UnpatchSelf(); } catch { /* teardown best-effort */ }
        _harmony = null;
        _patched = false;
        _log?.Invoke("[KeyBlock] removed — no blocked bindings / capture; Rewired input path detour-free");
    }

    // __0 = positional injection — avoids name mismatch between "key" (UnityEngine.Input) and "keyCode" (Rewired.Keyboard)
    private static bool PrefixBlock(int __0, ref bool __result)
    {
        // Perf harness: count + time this hook — it fires at Rewired's render-frame poll rate
        // whenever installed (its call frequency is unknowable statically). No-op unless PERFHUD.
        var perfT = Stellar.Abstractions.Diagnostics.PerfProbe.HookBegin();
        try
        {
            if (_captureMode) { __result = false; return false; }
            // Primary-key pre-filter: only queries about a key that participates in a blocked
            // binding pay the modifier read (6 Input.GetKey interop calls). Everything else
            // (movement keys, ability polls) exits on a HashSet miss.
            if (_blockedPrimaries.Count > 0 && _blockedPrimaries.Contains(__0)
                && _blocked.Contains((__0, (int)GetCurrentModifiers())))
            {
                __result = false;
                return false;
            }
            return true;
        }
        finally
        {
            Stellar.Abstractions.Diagnostics.PerfProbe.HookEndRewired(perfT);
        }
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
