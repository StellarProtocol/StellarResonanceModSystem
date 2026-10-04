using System;
using System.Collections.Generic;

namespace Stellar.Abstractions.Domain;

/// <summary>A download a plugin asks for: one https file or zip, verified, placed under the plugin's own data folder
/// (<c>IPluginDownloads.DataFolder</c> — the same folder <c>IPluginDataStore</c> uses).</summary>
/// <param name="Url">Absolute https URL. Anything else is refused.</param>
/// <param name="Sha256">The expected SHA-256 of the downloaded bytes: 64 hex characters, compared case-insensitively.
/// Checked before anything is written; a mismatch writes nothing.</param>
/// <param name="MaxBytes">Upper bound on the downloaded (for a zip: compressed) bytes. The whole download is held in
/// memory until it is verified, so keep this to what you actually need.</param>
/// <param name="TargetPath">Relative to the plugin's data folder, "/"-separated, and never escaping it. For a plain
/// download it is the destination FILE path (e.g. <c>"reshade/ReShade64.dll"</c>); for a zip it is the destination
/// FOLDER. A zip download REPLACES that whole folder (atomically), so keep any other files of yours elsewhere.</param>
/// <param name="ExtractZip">True: the download is a zip, extracted into <paramref name="TargetPath"/>. False: the bytes
/// are written as one file at <paramref name="TargetPath"/>.</param>
/// <param name="IncludePrefixes">Zip only: extract only entries whose path starts with one of these prefixes (null =
/// every entry). The prefix is KEPT in the output path: with <c>["Shaders/"]</c> and target <c>"packs/std"</c>, the entry
/// <c>Shaders/Bloom.fx</c> lands at <c>packs/std/Shaders/Bloom.fx</c>; <c>Textures/x.png</c> is skipped.</param>
public sealed record DownloadRequest(Uri Url, string Sha256, long MaxBytes, string TargetPath, bool ExtractZip,
    IReadOnlyList<string>? IncludePrefixes = null);

/// <summary>Outcome of a download.</summary>
/// <param name="Ok">Whether the download succeeded.</param>
/// <param name="Folder">Where the content landed when <paramref name="Ok"/> is true: the exact FILE path for
/// a plain (non-zip) download, or the extraction root FOLDER for a zip. Null when <paramref name="Ok"/> is
/// false.</param>
/// <param name="Error">A short, player-safe reason when <paramref name="Ok"/> is false; null on success.</param>
public sealed record DownloadResult(bool Ok, string? Folder, string? Error);
