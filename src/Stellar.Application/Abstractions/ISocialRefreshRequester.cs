namespace Stellar.Application.Abstractions;

/// <summary>Outbound port: drives the game's own self social-data request (implemented in
/// Infrastructure). Application declares it; Host wires the Infrastructure adapter.</summary>
public interface ISocialRefreshRequester
{
    /// <summary>Originate a Social.GetSocialData request for the given charId (self).</summary>
    void RequestSelfSocialData(long charId);
}
