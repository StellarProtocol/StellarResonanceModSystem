using System;
using System.Collections.Generic;

namespace Stellar.Abstractions.Domain;

/// <summary>A download a plugin asks for: one https file or zip, verified, placed under the plugin's own data folder.</summary>
public sealed record DownloadRequest(Uri Url, string Sha256, long MaxBytes, string TargetFolder, bool ExtractZip,
    IReadOnlyList<string>? IncludePrefixes = null);

/// <summary>Outcome of a download.</summary>
public sealed record DownloadResult(bool Ok, string? Folder, string? Error);
