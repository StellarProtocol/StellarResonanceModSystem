using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// A live 3D preview of the local player wearing an ARBITRARY saved outfit (region→fashionId), rendered
/// by the game's own UI-model pipeline into a render texture a <c>RenderTextureHostElement</c> displays.
/// Unlike <see cref="IEntityPortrait"/> (which shows an entity's CURRENT worn outfit), this dresses a fresh
/// self model with the outfit you pass — for a wardrobe hover/click preview. The model is created
/// asynchronously; <see cref="Texture"/> stays null for a few frames until the game delivers it.
/// </summary>
public interface IWardrobePreview
{
    /// <summary>True while a preview subject is active (model created or being created).</summary>
    bool IsActive { get; }

    /// <summary>Show a preview of <paramref name="self"/> wearing <paramref name="outfit"/>
    /// (region→fashionId; 0 = empty slot). A non-player id hides the preview instead.</summary>
    /// <param name="self">The local player entity (its outfit is overridden by <paramref name="outfit"/>).</param>
    /// <param name="outfit">Region→fashionId map to dress the model with.</param>
    /// <param name="dyes">Optional per-region, per-area dye colours: region → (<c>EFashionColorAreaType</c>
    /// area code 1..16 → RGB triple, each channel 0..1). When present the piece is tinted per area — each
    /// colour lands on its real area so multi-area pieces render correctly; omitted regions/areas render in
    /// the fashion's default colour.</param>
    void Show(EntityId self, IReadOnlyDictionary<int, int> outfit,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>>? dyes = null);

    /// <summary>Hide the preview and release the model back to the game's pool.</summary>
    void Hide();

    /// <summary>The boxed <c>UnityEngine.Texture</c> to display, or null while inactive / still loading.</summary>
    object? Texture { get; }

    /// <summary>Report the display box's current pixel size so the preview sizes its render texture to match.</summary>
    /// <param name="width">Display box width in pixels.</param>
    /// <param name="height">Display box height in pixels.</param>
    void SetViewport(int width, int height);

    /// <summary>Rotate the preview subject. <paramref name="dx"/>/<paramref name="dy"/> are pointer-drag
    /// deltas in pixels (horizontal drag spins the model).</summary>
    /// <param name="dx">Horizontal pointer-drag delta.</param>
    /// <param name="dy">Vertical pointer-drag delta.</param>
    void Orbit(float dx, float dy);

    /// <summary>Zoom the preview camera. Positive <paramref name="delta"/> moves closer (e.g. scroll-wheel up).</summary>
    /// <param name="delta">Scroll-wheel delta; positive = closer.</param>
    void Zoom(float delta);

    /// <summary>Pan the preview camera (shift+drag). <paramref name="dx"/>/<paramref name="dy"/> are pointer-drag
    /// deltas in pixels; the look-at point slides so different parts of the model can be framed.</summary>
    /// <param name="dx">Horizontal pointer-drag delta.</param>
    /// <param name="dy">Vertical pointer-drag delta.</param>
    void Pan(float dx, float dy);
}
