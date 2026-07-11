using System;
using System.Diagnostics;
using System.IO;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.BepInExAdapters;

/// <summary>
/// <see cref="IInstallInfo"/> over BepInEx's <c>Paths</c> + the current process.
/// Values are read once at construction; any failure yields null so region
/// detection reports <c>Unknown</c> instead of throwing during boot.
/// </summary>
internal sealed class BepInExInstallInfo : IInstallInfo
{
    public string? GameRootPath { get; }
    public string? ExecutableName { get; }

    public BepInExInstallInfo()
    {
        GameRootPath = TryGet(static () => BepInEx.Paths.GameRootPath);
        ExecutableName = TryGet(static () => Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName));
    }

    private static string? TryGet(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
