using System;

namespace Stellar.Abstractions.Domain;

/// <summary>Scene layers a plugin can ask the framework to hide.</summary>
[Flags]
public enum VisibilityLayers
{
    /// <summary>Nothing hidden.</summary>
    None = 0,
    /// <summary>The game's own HUD and UI.</summary>
    GameHud = 1,
    /// <summary>Stellar's overlay windows and HUD elements.</summary>
    StellarOverlay = 2,
    /// <summary>Names and nameplates above characters.</summary>
    Nameplates = 4,
    /// <summary>Other players' characters.</summary>
    OtherPlayers = 8,
    /// <summary>Modifier for <see cref="OtherPlayers"/>: keep party members visible.</summary>
    KeepParty = 16,
}

/// <summary>Image file format for a capture.</summary>
public enum CaptureFormat
{
    /// <summary>Lossless PNG.</summary>
    Png,
    /// <summary>JPEG with <see cref="CaptureRequest.JpgQuality"/>.</summary>
    Jpg,
}

/// <summary>Kind of game-owned photo mode.</summary>
public enum PhotoModeKind
{
    /// <summary>Not in a game photo mode.</summary>
    None,
    /// <summary>The game's camera-frame screen.</summary>
    CameraFrame,
    /// <summary>The game's selfie camera.</summary>
    Selfie,
}

/// <summary>A request to capture the screen to a file.</summary>
public sealed record CaptureRequest
{
    /// <summary>Supersampling factor: 1, 2 or 4.</summary>
    public int Scale { get; init; } = 2;
    /// <summary>Output format.</summary>
    public CaptureFormat Format { get; init; } = CaptureFormat.Png;
    /// <summary>JPEG quality 1–100 (ignored for PNG).</summary>
    public int JpgQuality { get; init; } = 92;
    /// <summary>Target folder; created if missing.</summary>
    public string Directory { get; init; } = "";
    /// <summary>File name without extension.</summary>
    public string FileStem { get; init; } = "";
    /// <summary>Layers hidden for the capture frame only.</summary>
    public VisibilityLayers HideDuringCapture { get; init; }
}

/// <summary>Outcome of a capture.</summary>
/// <param name="Success">True when the file was written.</param>
/// <param name="Path">Written file path, or null.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Error">Player-readable error, or null.</param>
public sealed record CaptureResult(bool Success, string? Path, int Width, int Height, string? Error)
{
    /// <summary>A successful result.</summary>
    public static CaptureResult Ok(string path, int width, int height) => new(true, path, width, height, null);
    /// <summary>A failed result.</summary>
    public static CaptureResult Fail(string error) => new(false, null, 0, 0, error);
}
