using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>The renderer side of looks. <c>Apply(null)</c> disables our overrides.</summary>
internal interface ILookBackend
{
    LookCapabilities Capabilities { get; }
    void Apply(LookSettings? settings);
    /// <summary>Camera → local player distance in meters, or null when unknown.</summary>
    float? MeasureFocusDistance();
}
