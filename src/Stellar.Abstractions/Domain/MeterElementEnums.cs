namespace Stellar.Abstractions.Domain;

/// <summary>Battle-Imagine icon size in a meter row.</summary>
public enum ImagineSize
{
    /// <summary>~18 px icons (today's look).</summary>
    Small,
    /// <summary>~30 px icons for readability.</summary>
    Large,
}

/// <summary>Where the Battle-Imagine cluster sits in a meter row.</summary>
public enum ImaginePosition
{
    /// <summary>Trailing the top line, right of share % (today's look).</summary>
    TopRight,
    /// <summary>A dedicated column on the right spanning both row lines.</summary>
    RightColumn,
    /// <summary>Leading the row, before the rank number.</summary>
    Left,
}
