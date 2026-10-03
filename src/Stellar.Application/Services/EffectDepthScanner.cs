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
    // a preceding ':' so it never fires on the unrelated HLSL system-value semantic "SV_Depth".
    private static readonly Regex DepthSemantic = new(@":\s*DEPTH\b", RegexOptions.Compiled);

    /// <summary>True when <paramref name="fxSource"/> (with "//" line comments stripped) references the ReShade
    /// depth buffer, the linearized-depth helper, or a texture bound to the DEPTH semantic.</summary>
    internal static bool UsesDepth(string fxSource)
    {
        var stripped = StripLineComments(fxSource);
        return stripped.Contains(DepthBufferToken, StringComparison.Ordinal)
            || stripped.Contains(LinearizedDepthToken, StringComparison.Ordinal)
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
