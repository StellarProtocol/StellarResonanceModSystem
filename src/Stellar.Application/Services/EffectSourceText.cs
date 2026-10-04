using System;
using System.Text.RegularExpressions;

namespace Stellar.Application.Services;

/// <summary>Text helpers shared by the pure effect-source scans (<see cref="EffectDepthScanner"/>,
/// <see cref="EffectSizeLockScanner"/>). No I/O.</summary>
internal static class EffectSourceText
{
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary><paramref name="source"/> with every <c>// …</c> line comment cut and every <c>/* … */</c> block comment
    /// replaced by a space (line comments first, as the depth scan always did).</summary>
    internal static string WithoutComments(string source) => BlockComment.Replace(StripLineComments(source), " ");

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
