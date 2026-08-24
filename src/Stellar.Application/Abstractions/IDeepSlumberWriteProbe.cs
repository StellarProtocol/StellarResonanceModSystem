using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound port (implemented in Infrastructure over the game's <c>season_talent</c> Lua VM;
/// InternalsVisibleTo grants access) for the primitive Deep-Slumber writes. Each returns the game's
/// bare error code — <c>0</c> = ok. The <c>currentItemId</c> on unsocket is toast-only (the server
/// request carries the nodeId alone).</summary>
internal interface IDeepSlumberWriteProbe
{
    bool IsResolved { get; }
    Task<int> EnableLineAsync(int areaId, CancellationToken ct);
    Task<int> SocketFactorAsync(int nodeId, int itemId, CancellationToken ct);
    Task<int> UnsocketFactorAsync(int nodeId, int currentItemId, CancellationToken ct);
}
