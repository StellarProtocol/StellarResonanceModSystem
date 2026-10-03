namespace Stellar.Abstractions.Domain;

/// <summary>A lamp added through <see cref="Services.ILights.AddLamp"/>. <see cref="None"/> (0) is never a real lamp.</summary>
/// <param name="Value">The framework's id for the lamp (positive while it exists).</param>
public readonly record struct LampId(int Value)
{
    /// <summary>No lamp: what <see cref="Services.ILights.AddLamp"/> returns when it could not add one.</summary>
    public static readonly LampId None = new(0);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Value == 0;
}

/// <summary>Everything about one lamp. A lamp is a point light that joins the game's own clustered lights: it makes a real
/// pool of light on the ground and the scene around <see cref="Position"/>, and — while
/// <see cref="Services.ILights.PeopleLevel"/> is above 0 — an even colour wash on characters within <see cref="Range"/>.</summary>
/// <param name="Position">World position of the lamp.</param>
/// <param name="Color">Linear colour, 0–1 per channel.</param>
/// <param name="Strength">Light intensity, clamped to 0–<see cref="LightLimits.MaxStrength"/> (the game's own lamps use
/// roughly 1–40; on characters it saturates above ~10).</param>
/// <param name="Range">Reach in metres, clamped to <see cref="LightLimits.MinRange"/>–<see cref="LightLimits.MaxRange"/>;
/// nothing beyond it is lit.</param>
/// <param name="Enabled">Off keeps the lamp (and its settings) but removes its light.</param>
public readonly record struct LampSettings(Position3D Position, RgbColor Color, float Strength, float Range, bool Enabled);

/// <summary>A per-person key light: shades the person's own model from one side, as seen from the camera that draws
/// them. Only that person changes.</summary>
/// <param name="Direction">Degrees around the camera's view, −180…180: 0 = from the camera, positive = from the camera's
/// right, negative = from its left, ±180 = from behind the person.</param>
/// <param name="Height">Degrees above (positive) or below (negative) the camera's eye line, −89…89.</param>
public readonly record struct KeyLight(float Direction, float Height);

/// <summary>A per-person coloured rim (the game's Fresnel edge). Shows on hair, headwear and weapons — the body's shader
/// has none.</summary>
/// <param name="Color">Linear colour, 0–1 per channel.</param>
/// <param name="Strength">0–<see cref="LightLimits.MaxRimStrength"/>; 0 shows no rim.</param>
public readonly record struct RimLight(RgbColor Color, float Strength);

/// <summary>The light set on one person: a key light, a rim, both or neither (neither = the person as the game drew
/// them).</summary>
/// <param name="Key">The key light, or null for none.</param>
/// <param name="Rim">The rim, or null for none.</param>
public readonly record struct PersonLight(KeyLight? Key, RimLight? Rim)
{
    /// <summary>No key light and no rim.</summary>
    public static PersonLight None => default;

    /// <summary>True when neither a key light nor a rim is set.</summary>
    public bool IsNone => Key is null && Rim is null;
}

/// <summary>The limits <see cref="Services.ILights"/> applies.</summary>
public static class LightLimits
{
    /// <summary>The most lamps one plugin can have at once (8 lamps were measured to cost no frame time); further
    /// <see cref="Services.ILights.AddLamp"/> calls return <see cref="LampId.None"/>.</summary>
    public const int MaxLampsPerPlugin = 8;

    /// <summary>The highest <see cref="Services.ILights.PeopleLevel"/> (the game's character-lamp multiplier; 1–2 is a
    /// natural look, 20 a strong wash).</summary>
    public const float MaxPeopleLevel = 20f;

    /// <summary>The highest <see cref="LampSettings.Strength"/>.</summary>
    public const float MaxStrength = 200f;

    /// <summary>The shortest <see cref="LampSettings.Range"/>, in metres.</summary>
    public const float MinRange = 0.5f;

    /// <summary>The longest <see cref="LampSettings.Range"/>, in metres.</summary>
    public const float MaxRange = 30f;

    /// <summary>The highest <see cref="RimLight.Strength"/>.</summary>
    public const float MaxRimStrength = 2f;
}
