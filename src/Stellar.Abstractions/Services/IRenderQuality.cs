using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Abstractions.Services;

/// <summary>
/// Raises the game's render quality (supersampling + TAA, high shadows) while a token is held. Shared by every
/// plugin, so two plugins never fight over the same game settings. Main thread only.
/// </summary>
/// <remarks>
/// The game re-applies its own quality grade on scene changes and from its settings panel; the framework
/// re-asserts every held lever after those (event-driven, writing only values that differ). Tokens a plugin still
/// holds are released when it unloads.
/// </remarks>
public interface IRenderQuality
{
    /// <summary>
    /// Raises render quality until the returned token is disposed. Requests are reference-counted as a union: any
    /// held token asking for supersampling keeps it on. The game's own values are captured before the first write
    /// and restored when the last token asking for a lever goes. Disposing a token twice is harmless.
    /// </summary>
    IDisposable Request(RenderQualityRequest request);
    /// <summary>The values the game's pipeline holds right now (read live).</summary>
    RenderQualityState Live { get; }
    /// <summary>Which levers resolved on this client.</summary>
    RenderQualityCapabilities Capabilities { get; }
}
