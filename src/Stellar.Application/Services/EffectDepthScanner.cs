using System;
using System.Text.RegularExpressions;

namespace Stellar.Application.Services;

/// <summary>
/// Pure text scan deciding whether a ReShade effect (.fx) source reads the depth buffer, so a technique can be
/// flagged <c>UsesDepth</c> for the capture planner without parsing HLSL. No I/O.
/// </summary>
internal static class EffectDepthScanner
{
    private const string DepthBufferToken = "ReShade::DepthBuffer";
    private const string LinearizedDepthToken = "GetLinearizedDepth";

    // Matches a texture's "DEPTH" semantic annotation (e.g. `texture2D t : DEPTH;`) — deliberately anchored on
    // a preceding ':' so it never fires on the unrelated HLSL system-value semantic "SV_Depth". Case-insensitive
    // like every token here (semantics are case-insensitive in HLSL; erring toward "uses depth" is the safe side).
    private static readonly Regex DepthSemantic = new(@":\s*DEPTH\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="fxSource"/> (with "//" line comments stripped) references the ReShade
    /// depth buffer, the linearized-depth helper, or a texture bound to the DEPTH semantic, in any letter case.
    /// False for null — a caller that could not READ the source must decide for itself (it should assume depth).</summary>
    internal static bool UsesDepth(string? fxSource)
    {
        if (fxSource is null) return false;
        var stripped = StripLineComments(fxSource);
        return stripped.Contains(DepthBufferToken, StringComparison.OrdinalIgnoreCase)
            || stripped.Contains(LinearizedDepthToken, StringComparison.OrdinalIgnoreCase)
            || DepthSemantic.IsMatch(stripped);
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
