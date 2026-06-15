namespace Stellar.Abstractions.Domain;

/// <summary>
/// Normalized atlas sub-rect (0..1, origin bottom-left, Unity texture-space convention) for
/// <see cref="Stellar.Abstractions.Services.SpriteElement"/>. A plain BCL struct so the Abstractions
/// layer carries no Unity dependency — the renderer maps it to <c>UnityEngine.Rect</c>.
/// </summary>
public readonly record struct UvRect(float X, float Y, float W, float H);
