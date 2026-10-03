using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Stellar.Infrastructure.Rendering;

/// <summary>Pure <c>#include</c> flattening for the depth scan: each <c>#include "x"</c> / <c>&lt;x&gt;</c> line is
/// replaced by the included file's own flattened text, so depth use hidden in a shared .fxh is visible to
/// <c>EffectDepthScanner</c>. Each file is inlined at most once (cycles and repeats are skipped — the scan only needs
/// presence). Returns null — "cannot tell, assume depth" — when the root or any include cannot be resolved or read,
/// or nesting goes past <see cref="MaxDepth"/>. Preprocessor conditionals are ignored: every include is followed.</summary>
internal static class EffectIncludeFlattener
{
    internal const int MaxDepth = 16;

    private static readonly Regex IncludeLine = new(
        @"^[ \t]*#[ \t]*include[ \t]*[""<]([^"">\r\n]+)["">]", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <param name="read">Path → text, or null when unreadable.</param>
    /// <param name="resolveInclude">(including file, include name) → path, or null when not found.</param>
    internal static string? Flatten(string rootPath, Func<string, string?> read, Func<string, string, string?> resolveInclude)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        return Append(rootPath, 0, new Context(read, resolveInclude, visited, output)) ? output.ToString() : null;
    }

    private readonly record struct Context(
        Func<string, string?> Read, Func<string, string, string?> Resolve, HashSet<string> Visited, StringBuilder Output);

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
            var included = ctx.Resolve(path, match.Groups[1].Value.Trim());
            if (included is null || !Append(included, depth + 1, ctx)) return false;
            ctx.Output.Append('\n');
        }
        ctx.Output.Append(text, copied, text.Length - copied);
        return true;
    }
}
