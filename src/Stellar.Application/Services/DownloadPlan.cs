using System;
using System.Collections.Generic;
using System.IO;

namespace Stellar.Application.Services;

/// <summary>
/// Pure path-safety rules for <c>IPluginDownloads</c>: where a requested target folder resolves to under the
/// plugin's data folder, and which entries of an extracted zip are allowed out. No I/O — every member is a
/// string computation only, so the real download service (Infrastructure) can be driven and tested without
/// touching disk.
/// </summary>
internal static class DownloadPlan
{
    /// <summary>Resolves <paramref name="targetFolder"/> under <paramref name="dataFolder"/>, or null when
    /// <paramref name="targetFolder"/> is empty, rooted, or contains a "\", a ":" or a ".."/"." segment.</summary>
    internal static string? ResolveTarget(string dataFolder, string targetFolder)
    {
        if (string.IsNullOrEmpty(targetFolder) || targetFolder.Contains('\\') || HasUnsafeSegment(targetFolder))
            return null;
        return Path.Combine(dataFolder, targetFolder);
    }

    /// <summary>Maps one zip entry to a relative, "/"-separated output path, or null when the entry is a
    /// directory (name ends with "/"), escapes the extraction folder ("..", rooted, or a ":" segment), or
    /// falls outside every prefix in <paramref name="includePrefixes"/> (null allows every file entry).</summary>
    internal static string? MapZipEntry(string entryName, IReadOnlyList<string>? includePrefixes)
    {
        if (string.IsNullOrEmpty(entryName)) return null;
        var normalized = entryName.Replace('\\', '/');
        if (normalized.EndsWith("/", StringComparison.Ordinal)) return null;
        if (HasUnsafeSegment(normalized)) return null;
        if (includePrefixes is not null && !MatchesAnyPrefix(normalized, includePrefixes)) return null;
        return normalized;
    }

    private static bool MatchesAnyPrefix(string normalized, IReadOnlyList<string> prefixes)
    {
        foreach (var raw in prefixes)
        {
            var prefix = raw.Replace('\\', '/').TrimEnd('/');
            if (prefix.Length == 0) continue;
            if (normalized == prefix || normalized.StartsWith(prefix + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool HasUnsafeSegment(string normalizedPath)
    {
        if (normalizedPath.Length == 0 || normalizedPath[0] == '/' || normalizedPath.Contains(':'))
            return true;
        foreach (var segment in normalizedPath.Split('/'))
        {
            if (segment is "." or "..")
                return true;
        }
        return false;
    }
}
