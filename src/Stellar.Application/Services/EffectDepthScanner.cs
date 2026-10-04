using System;
using System.Text.RegularExpressions;

namespace Stellar.Application.Services;

/// <summary>
/// Pure text scan deciding whether a ReShade effect (.fx) source reads the depth buffer, so a technique can be
/// flagged <c>UsesDepth</c> for the capture planner without parsing HLSL. No I/O.
/// </summary>
internal static class EffectDepthScanner
{
    private const string DepthBufferToken = "DepthBuffer"; // ReShade::DepthBuffer, a pack's own DepthBufferTex, ...
    private const string LinearizedDepthToken = "GetLinearizedDepth";

    // Matches a texture's "DEPTH" semantic (`texture2D t : DEPTH;`) — anchored on the ':' so it never fires on the
    // system-value "SV_Depth", a depth OUTPUT that does not read the game's depth. Case-insensitive like every token
    // here (semantics are case-insensitive in HLSL).
    private static readonly Regex DepthSemantic = new(@":\s*DEPTH\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ReShadeNamespace = new(@"\bnamespace\s+ReShade\s*\{", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="fxSource"/> (comments and any <c>namespace ReShade { … }</c> copy of the standard
    /// header stripped) references a depth buffer
    /// (<c>DepthBuffer</c>), the linearized-depth helper or a texture bound to the DEPTH semantic, in any letter case
    /// (SV_Depth, a depth output, does not count).
    /// False for null — a caller that could not READ the source must decide for itself (it should assume depth).</summary>
    internal static bool UsesDepth(string? fxSource)
    {
        if (fxSource is null) return false;
        var stripped = WithoutReShadeNamespaces(BlockComment.Replace(StripLineComments(fxSource), " "));
        return stripped.Contains(DepthBufferToken, StringComparison.OrdinalIgnoreCase)
            || stripped.Contains(LinearizedDepthToken, StringComparison.OrdinalIgnoreCase)
            || DepthSemantic.IsMatch(stripped);
    }

    /// <summary>Drops every <c>namespace ReShade { … }</c> block. Such a block is a copy of ReShade's standard header
    /// (AcerolaFX ships one in <c>AcerolaFX_Common.fxh</c>): it DECLARES the depth texture and defines
    /// <c>GetLinearizedDepth</c>, which is not use. An effect that reads depth references it from outside the block
    /// (<c>ReShade::DepthBuffer</c>), which stays visible. Brace-matched; an unclosed block runs to the end.</summary>
    private static string WithoutReShadeNamespaces(string source)
    {
        var match = ReShadeNamespace.Match(source);
        if (!match.Success) return source;
        var output = new System.Text.StringBuilder(source.Length);
        var copied = 0;
        while (match.Success)
        {
            output.Append(source, copied, match.Index - copied);
            var depth = 1;
            var i = match.Index + match.Length;
            for (; i < source.Length && depth > 0; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}') depth--;
            }
            copied = i;
            match = ReShadeNamespace.Match(source, copied);
        }
        output.Append(source, copied, source.Length - copied);
        return output.ToString();
    }

    private static string StripLineComments(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var index = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (index >= 0)
                lines[i] = lines[i][..index];
        }
        return string.Join('\n', lines);
    }
}
