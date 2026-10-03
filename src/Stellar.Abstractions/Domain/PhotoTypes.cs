using System;
using System.Collections.Generic;

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
    /// <summary>The local player's own character, their pet and their summons — hidden on this screen only.</summary>
    Self = 32,
    /// <summary>Effects (skills, buffs, hits, ground areas) caused by the local player or their summons.</summary>
    EffectsMine = 64,
    /// <summary>Effects caused by party members or their summons.</summary>
    EffectsParty = 128,
    /// <summary>Effects caused by other players outside the party, or their summons.</summary>
    EffectsOthers = 256,
    /// <summary>Effects caused by monsters and bosses, including their warning areas.</summary>
    EffectsMonsters = 512,
}

/// <summary>Named combinations of <see cref="VisibilityLayers"/>.</summary>
public static class VisibilityLayerSets
{
    /// <summary>All four effect layers. An effect with no caster (scenery) belongs to none of them and is never hidden.</summary>
    public const VisibilityLayers Effects = VisibilityLayers.EffectsMine | VisibilityLayers.EffectsParty |
                                            VisibilityLayers.EffectsOthers | VisibilityLayers.EffectsMonsters;
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
    /// <summary>
    /// The photo's shape (for example 9:16 portrait); null = the window's own shape (the default, unchanged behaviour).
    /// The camera renders straight into a target of that shape — never a crop. Its long side is the window's long side
    /// × <see cref="Scale"/> and the short side follows the shape (see <see cref="CaptureSizing.OutputSize"/>). The photo
    /// frames the largest rectangle of the shape centred in the on-screen view, so a frame guide drawn with
    /// <see cref="CaptureSizing.GuideRect"/> shows exactly what will be in the photo. Must be a valid shape
    /// (<see cref="CaptureAspect.IsValid"/>), or the capture fails with a readable error.
    /// </summary>
    public CaptureAspect? Aspect { get; init; }
    /// <summary>Draw ReShade's active effects into the photo when ReShade is available and enabled (default true).
    /// In a non-screen <see cref="Aspect"/>, techniques that use depth are skipped for this photo.</summary>
    public bool ApplyReShade { get; init; } = true;
}

/// <summary>A photo shape as a width:height ratio of whole numbers, for example 9:16 (portrait) or 21:9 (wide).</summary>
/// <param name="Width">Ratio width (positive).</param>
/// <param name="Height">Ratio height (positive).</param>
public readonly record struct CaptureAspect(int Width, int Height)
{
    /// <summary>The longest ratio allowed: a shape may be at most 4 times as long as it is wide (1:4 … 4:1).</summary>
    public const int MaxRatio = 4;

    /// <summary>True when both sides are positive and the shape is within 1:4 … 4:1.</summary>
    public bool IsValid => Width > 0 && Height > 0 &&
                           (long)Math.Max(Width, Height) <= (long)MaxRatio * Math.Min(Width, Height);

    /// <summary>Width ÷ height (0 when <see cref="Height"/> is not positive).</summary>
    public double Ratio => Height > 0 ? (double)Width / Height : 0d;

    /// <summary>"9:16" style text.</summary>
    public override string ToString() => Width + ":" + Height;
}

/// <summary>An image size in pixels.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct CaptureSize(int Width, int Height)
{
    /// <summary>True when either side is zero or negative (no image).</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>A rectangle in normalized screen coordinates: 0..1, origin at the TOP-left of the window.</summary>
/// <param name="X">Left edge (0 = window left).</param>
/// <param name="Y">Top edge (0 = window top).</param>
/// <param name="Width">Width as a fraction of the window width.</param>
/// <param name="Height">Height as a fraction of the window height.</param>
public readonly record struct NormalizedRect(float X, float Y, float Width, float Height);

/// <summary>Outcome of a capture.</summary>
/// <param name="Success">True when the file was written.</param>
/// <param name="Path">Written file path, or null.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Error">Player-readable error, or null.</param>
public sealed record CaptureResult(bool Success, string? Path, int Width, int Height, string? Error)
{
    /// <summary>Player-readable remarks about a capture that still succeeded — for example "ReShade was not ready — photo
    /// taken without it." Empty when there is nothing to say (always empty on failure).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>A successful result.</summary>
    public static CaptureResult Ok(string path, int width, int height) => new(true, path, width, height, null);
    /// <summary>A failed result.</summary>
    public static CaptureResult Fail(string error) => new(false, null, 0, 0, error);
}
