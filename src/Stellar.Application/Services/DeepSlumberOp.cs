namespace Stellar.Application.Services;

internal enum DeepSlumberOpKind { EnableLine, SocketFactor, UnsocketFactor }

/// <summary>One primitive Deep-Slumber write. <see cref="Key"/> is an areaId (EnableLine) or a nodeId
/// (Socket/Unsocket); <see cref="ItemId"/> is the factor to socket; <see cref="CurrentItemId"/> is the
/// factor being removed (Unsocket, toast lookup).</summary>
internal sealed record DeepSlumberOp(DeepSlumberOpKind Kind, int Key, int ItemId, int CurrentItemId)
{
    public static DeepSlumberOp EnableLine(int areaId) => new(DeepSlumberOpKind.EnableLine, areaId, 0, 0);
    public static DeepSlumberOp Socket(int nodeId, int itemId) => new(DeepSlumberOpKind.SocketFactor, nodeId, itemId, 0);
    public static DeepSlumberOp Unsocket(int nodeId, int currentItemId) => new(DeepSlumberOpKind.UnsocketFactor, nodeId, 0, currentItemId);
}
