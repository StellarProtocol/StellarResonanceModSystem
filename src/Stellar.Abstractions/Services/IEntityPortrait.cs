using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Live, posed 3D portrait of a player entity — full body, real outfit, idle-animated — rendered by the game's
/// own UI-model pipeline into a render texture that a <see cref="RenderTextureHostElement"/> displays. Works for
/// the local player and any other player (the subject is created from the player's social data, the same way the
/// game's own character preview windows do it). The plugin calls <see cref="Show"/> when its portrait slot opens
/// on an entity and reads <see cref="Texture"/> (the boxed <c>UnityEngine.Texture</c>) to display it.
/// </summary>
public interface IEntityPortrait
{
    /// <summary>True while a portrait subject is active (model created or being created).</summary>
    bool IsActive { get; }

    /// <summary>Show the portrait for an entity. Non-player ids hide the portrait instead. The model is created
    /// asynchronously — <see cref="Texture"/> stays null for a few frames until the game delivers it.</summary>
    void Show(EntityId entity);

    /// <summary>Hide the portrait and release the model back to the game's pool.</summary>
    void Hide();

    /// <summary>The boxed <c>UnityEngine.Texture</c> to display, or null while inactive / still loading.</summary>
    object? Texture { get; }

    /// <summary>Rotate the portrait subject. <paramref name="dx"/>/<paramref name="dy"/> are pointer-drag deltas
    /// in pixels (horizontal drag spins the model; vertical drag is currently ignored).</summary>
    void Orbit(float dx, float dy);

    /// <summary>Zoom the portrait view. Positive <paramref name="delta"/> moves closer (e.g. scroll-wheel up).</summary>
    void Zoom(float delta);

    /// <summary>Pan the portrait camera (shift+drag). <paramref name="dx"/>/<paramref name="dy"/> are pointer-drag
    /// deltas in pixels; the look-at point slides so different parts of the model can be framed.</summary>
    void Pan(float dx, float dy);

    /// <summary>Report the display box's current pixel size so the portrait sizes its render texture + projection
    /// to match — it then fills the (resizable) pane top-to-bottom with no letterbox or stretch.</summary>
    void SetViewport(int width, int height);
}
