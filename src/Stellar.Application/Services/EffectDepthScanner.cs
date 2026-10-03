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

    /// <summary>True when <paramref name="fxSource"/> (with "//" line comments stripped) references a depth buffer
    /// (<c>DepthBuffer</c>), the linearized-depth helper or a texture bound to the DEPTH semantic, in any letter case
    /// (SV_Depth, a depth output, does not count).
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
