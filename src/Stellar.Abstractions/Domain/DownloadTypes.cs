using System;
using System.Collections.Generic;

namespace Stellar.Abstractions.Domain;

/// <summary>A download a plugin asks for: one https file or zip, verified, placed under the plugin's own data folder.</summary>
public sealed record DownloadRequest(Uri Url, string Sha256, long MaxBytes, string TargetFolder, bool ExtractZip,
    IReadOnlyList<string>? IncludePrefixes = null);

/// <summary>Outcome of a download.</summary>
/// <param name="Ok">Whether the download succeeded.</param>
/// <param name="Folder">Where the content landed when <paramref name="Ok"/> is true: the exact FILE path for
/// a plain (non-zip) download, or the extraction root FOLDER for a zip. Null when <paramref name="Ok"/> is
/// false.</param>
/// <param name="Error">A short, player-safe reason when <paramref name="Ok"/> is false; null on success.</param>
public sealed record DownloadResult(bool Ok, string? Folder, string? Error);
