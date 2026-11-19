using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Exempts a member or type from STELLAR0007 (raw legacy uGUI <see cref="Text"/> create / write). Only for the
/// <see cref="LegacyText"/> path itself, helpers that MEASURE a legacy Text the caller already routed, and sites whose
/// strings are fixed ASCII / digits / glyphs — always with the reason.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Constructor
    | AttributeTargets.Property, Inherited = false)]
internal sealed class LegacyTextAllowedAttribute : Attribute
{
    public LegacyTextAllowedAttribute(string reason) { Reason = reason; }

    public string Reason { get; }
}

/// <summary>
/// THE legacy uGUI text path (fw issue #169). Every legacy <see cref="Text"/> is created by <see cref="Add"/> and every
/// string reaches it through <see cref="Set"/> / <see cref="SetStatic"/> (or <see cref="SetAscii"/> for fixed ASCII),
/// so a Thai string always lands on the TMP twin in the shipped Thai face (<see cref="ThaiTmpTwin"/>, with
/// <see cref="ThaiTmpHalo"/> for an outlined Text) instead of the OS fallback font, synthetic bold and no mark
/// positioning. The STELLAR0007 analyzer fails the build on a raw <c>AddComponent&lt;Text&gt;()</c> / <c>.text =</c>
/// anywhere else, so a new element can't skip it.
/// <para>Cost: one <see cref="GlyphScript.IsThai"/> scan per string CHANGE (callers diff first, as before); nothing is
/// built for a Text that never shows Thai; no per-frame work here.</para>
/// </summary>
[LegacyTextAllowed("the one legacy Text create / write path")]
internal static class LegacyText
{
    /// <summary>The one place a legacy uGUI <see cref="Text"/> is created.</summary>
    public static Text Add(GameObject go) => go.AddComponent<Text>();

    /// <summary>Show <paramref name="s"/> on <paramref name="t"/>: Thai → the twin kept in <paramref name="twin"/> (built
    /// on the first Thai string; an outlined Text gets its dark edge), anything else → the legacy Text (swapping a showing
    /// twin back out). True = the twin shows it. Call on a string change; while the twin shows, call
    /// <see cref="ThaiTmpTwin.Sync"/> per apply to mirror colour / size writes. <paramref name="ellipsis"/> /
    /// <paramref name="maxLines"/> = the label's fit mode, cut by TMP in the face that draws it.</summary>
    public static bool Set(Text t, string? s, ref ThaiTmpTwin? twin, bool ellipsis = false, int maxLines = 0)
    {
        // A Text first labelled by SetStatic and later re-written by a binding takes over that twin (never a second one).
        if (twin == null && StaticTwins.Count > 0 && StaticTwins.Remove(t.GetInstanceID(), out var st)) twin = st;
        if (ThaiTmpTwin.Route(ref twin, t, s, ellipsis, maxLines)) return true;
        t.text = s ?? string.Empty;
        return false;
    }

    /// <summary>A label set once at build (no binding follows): routed once; the twin keeps the build-time colour and
    /// size. For a label whose colour or size changes later, keep a twin field and use <see cref="Set"/>.</summary>
    public static void SetStatic(Text t, string? s)
    {
        ThaiTmpTwin? twin = null;
        if (!Set(t, s, ref twin)) return;
        if (StaticTwins.Count >= PruneAt) PruneStaticTwins();
        StaticTwins[t.GetInstanceID()] = twin!;
    }

    private const int PruneAt = 256;

    // Drops twins whose GameObject was destroyed with its window (a rebuilt dropdown list etc.). Rare: only once the
    // Thai-static set reaches PruneAt.
    private static void PruneStaticTwins()
    {
        var dead = new System.Collections.Generic.List<int>();
        foreach (var kv in StaticTwins) if (kv.Value.Go == null) dead.Add(kv.Key);
        foreach (var k in dead) StaticTwins.Remove(k);
    }

    // Twins built by SetStatic, by legacy Text instance id, so a later Set on the same Text reuses the twin instead of
    // building a second one over a hidden legacy Text. Holds Thai static labels only (rare); main thread only.
    private static readonly System.Collections.Generic.Dictionary<int, ThaiTmpTwin> StaticTwins = new();

    /// <summary>A string already routed by the caller (its own <see cref="Set"/> / twin Route returned false for it):
    /// write the legacy Text — e.g. the fitted cut of that string. No script scan.</summary>
    public static void SetRouted(Text t, string s) => t.text = s;

    /// <summary>Fixed ASCII / digits / glyph strings only (✕, ★, "Hex", counts, seconds, ranks) — never names or
    /// translated text. No script scan, no twin.</summary>
    public static void SetAscii(Text t, string s) => t.text = s;
}
