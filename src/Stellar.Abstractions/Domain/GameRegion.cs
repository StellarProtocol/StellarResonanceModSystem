namespace Stellar.Abstractions.Domain;

/// <summary>Game-server region a running client belongs to (distinct regional releases with independent servers and ID spaces).</summary>
public enum GameRegion
{
    /// <summary>No install marker matched and no config override is set. Upload plugins should withhold uploads.</summary>
    Unknown = 0,
    /// <summary>SEA release (Tencent, <c>StarSEA.exe</c>).</summary>
    Sea = 1,
    /// <summary>Japan release.</summary>
    Jp = 2,
}
