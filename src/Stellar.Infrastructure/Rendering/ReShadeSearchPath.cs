using System.IO;

namespace Stellar.Infrastructure.Rendering;

/// <summary>One entry of a ReShade search-path list. As in ReShade, a path ending in <c>**</c> is recursive.</summary>
internal readonly record struct ReShadeSearchPath(string Root, bool Recursive)
{
    private static readonly char[] Separators = { '/', '\\' };

    internal static ReShadeSearchPath Parse(string raw)
    {
        var path = raw.Trim().Trim('"').Trim();
        var recursive = path.EndsWith("**", System.StringComparison.Ordinal);
        if (recursive) path = path[..^2];
        var trimmed = path.TrimEnd(Separators);
        return new ReShadeSearchPath(trimmed.Length == 0 ? path : trimmed, recursive);
    }

    /// <summary>The recursive search-path form of an absolute <paramref name="folder"/> (<c>folder\**</c>).</summary>
    internal static string FormatRecursive(string folder)
    {
        var trimmed = folder.TrimEnd(Separators);
        return Path.Join(trimmed.Length == 0 ? folder : trimmed, "**");
    }
}
