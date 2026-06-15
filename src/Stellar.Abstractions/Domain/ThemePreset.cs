namespace Stellar.Abstractions.Domain;

/// <summary>Named palette family applied to every plugin window.</summary>
public enum ThemePreset
{
    /// <summary>The default balanced dark-on-glass theme shipped with Stellar.</summary>
    Default = 0,
    /// <summary>High-contrast dark theme for low-light environments.</summary>
    Dark    = 1,
    /// <summary>Light / bright theme for bright-monitor or accessibility preferences.</summary>
    Light   = 2,
    /// <summary>Crimson accent theme for a distinctive red-tinted appearance.</summary>
    Crimson = 3,
}
