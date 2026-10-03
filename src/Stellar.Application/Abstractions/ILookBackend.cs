using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>The renderer side of looks. <c>Apply(null)</c> disables our overrides.</summary>
internal interface ILookBackend
{
    LookCapabilities Capabilities { get; }
    void Apply(LookSettings? settings);
    /// <summary>Writes ONLY the depth-of-field focus distance of the active look (focus tracking; never a full re-apply).</summary>
    void UpdateFocus(float distance);
    /// <summary>Camera → local player distance in meters, or null when unknown.</summary>
    float? MeasureFocusDistance();
}
