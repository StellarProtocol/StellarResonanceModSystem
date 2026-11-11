using System;
using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

/// <summary>One parsed token of an allowlist <c>RectChild</c> outline spec (see <see cref="OutlineSpec"/>).</summary>
/// <param name="Path">'/'-separated sub-path under the element ("." = the element itself).</param>
/// <param name="Content">"*" — use the visible-drawable union of the subtree instead of the node's own rect.</param>
/// <param name="AxisX">Contributes its horizontal extent ("h:" turns this off).</param>
/// <param name="AxisY">Contributes its vertical extent ("w:" turns this off).</param>
/// <param name="Exclude">"-" — not a contributor: this subtree is skipped by every "*" union in the same spec.</param>
internal readonly record struct OutlineToken(string Path, bool Content, bool AxisX, bool AxisY, bool Exclude);

/// <summary>
/// Parser for the allowlist <c>RectChild</c> outline spec — pure (no Unity), so it is unit-tested. Syntax: tokens
/// separated by ';'. Each token is a sub-path, optionally prefixed by <c>h:</c> (vertical extent only) or <c>w:</c>
/// (horizontal only), then <c>*</c> (content union of that subtree). A token prefixed with <c>-</c> is an EXCLUDE: the
/// subtree it names is left out of every <c>*</c> union in the same spec (e.g. the quickbar's floating skill drawer:
/// <c>"*button_pos_group;-button_pos_group/10"</c>). Parsed once per distinct spec string (cached), so the outline
/// measure doesn't re-split the spec on every call.
/// </summary>
internal static class OutlineSpec
{
    private static readonly Dictionary<string, OutlineToken[]> Cache = new(StringComparer.Ordinal);

    public static IReadOnlyList<OutlineToken> Parse(string spec)
    {
        if (Cache.TryGetValue(spec, out var cached)) return cached;
        var raw = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokens = new OutlineToken[raw.Length];
        for (var i = 0; i < raw.Length; i++) tokens[i] = ParseToken(raw[i]);
        Cache[spec] = tokens;
        return tokens;
    }

    private static OutlineToken ParseToken(string token)
    {
        if (token.StartsWith("-"))
            return new OutlineToken(token.Substring(1).Trim(), Content: false, AxisX: false, AxisY: false, Exclude: true);
        bool axisX = true, axisY = true;
        if (token.StartsWith("h:")) { axisX = false; token = token.Substring(2); }
        else if (token.StartsWith("w:")) { axisY = false; token = token.Substring(2); }
        var content = token.StartsWith("*");
        return new OutlineToken(content ? token.Substring(1) : token, content, axisX, axisY, Exclude: false);
    }
}
