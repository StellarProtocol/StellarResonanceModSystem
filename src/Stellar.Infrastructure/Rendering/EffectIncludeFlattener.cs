using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Stellar.Infrastructure.Rendering;

/// <summary>Pure <c>#include</c> flattening for the depth scan: each <c>#include "x"</c> / <c>&lt;x&gt;</c> line is
/// replaced by the included file's own flattened text, so depth use hidden in a shared .fxh is visible to
/// <c>EffectDepthScanner</c>. Each file is inlined at most once (cycles and repeats are skipped — the scan only needs
/// presence). ReShade's own standard headers (<see cref="IsStandardHeader"/>) are skipped: they DECLARE the depth
/// texture and define <c>GetLinearizedDepth</c>, which is not use — an effect that reads depth calls or samples it in
/// its own text or in a pack's shared .fxh, which are flattened. Returns null — "cannot tell, assume depth" — when the root or any include cannot be resolved or read,
/// or nesting goes past <see cref="MaxDepth"/>. Preprocessor conditionals are ignored: every include is followed.</summary>
internal static class EffectIncludeFlattener
{
    internal const int MaxDepth = 16;

    private static readonly string[] StandardHeaders = { "ReShade.fxh", "ReShadeUI.fxh" };

    /// <summary>True for ReShade's standard headers, by file name, in any letter case.</summary>
    internal static bool IsStandardHeader(string includeName)
    {
        var fileName = includeName.Replace('\\', '/');
        fileName = fileName[(fileName.LastIndexOf('/') + 1)..];
        foreach (var header in StandardHeaders)
        {
            if (string.Equals(fileName, header, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly Regex IncludeLine = new(
        @"^[ \t]*#[ \t]*include[ \t]*[""<]([^"">\r\n]+)["">]", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <param name="read">Path → text, or null when unreadable.</param>
    /// <param name="resolveInclude">(including file, include name) → path, or null when not found.</param>
    internal static string? Flatten(string rootPath, Func<string, string?> read, Func<string, string, string?> resolveInclude) =>
        Flatten(rootPath, read, (file, name) => resolveInclude(file, name) is { } p ? new[] { p } : Array.Empty<string>());

    /// <summary>As above, but an include may resolve to several candidate files (an ambiguous name in a recursive
    /// search root): EVERY candidate is inlined, so a depth scan of the result ORs them. No candidate = null.</summary>
    internal static string? Flatten(string rootPath, Func<string, string?> read, Func<string, string, IReadOnlyList<string>> resolveCandidates)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        return Append(rootPath, 0, new Context(read, resolveCandidates, visited, output)) ? output.ToString() : null;
    }

    private readonly record struct Context(
        Func<string, string?> Read, Func<string, string, IReadOnlyList<string>> Resolve, HashSet<string> Visited, StringBuilder Output);

    private static bool Append(string path, int depth, Context ctx)
    {
        if (depth > MaxDepth) return false;
        if (!ctx.Visited.Add(path)) return true;
        var text = ctx.Read(path);
        if (text is null) return false;

        var copied = 0;
        foreach (Match match in IncludeLine.Matches(text))
        {
            ctx.Output.Append(text, copied, match.Index - copied);
            copied = match.Index + match.Length;
            var name = match.Groups[1].Value.Trim();
            if (IsStandardHeader(name)) continue;
            var candidates = ctx.Resolve(path, name);
            if (candidates.Count == 0) return false;
            foreach (var included in candidates)
            {
                if (!Append(included, depth + 1, ctx)) return false;
                ctx.Output.Append('\n');
            }
        }
        ctx.Output.Append(text, copied, text.Length - copied);
        return true;
    }
}
