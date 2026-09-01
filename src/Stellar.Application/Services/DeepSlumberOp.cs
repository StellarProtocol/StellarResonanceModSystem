namespace Stellar.Application.Services;

internal enum DeepSlumberOpKind { EnableLine, ResetNodes, SocketFactor, UnsocketFactor, ActivateNode }

/// <summary>One primitive Deep-Slumber write. <see cref="Key"/> is an areaId (EnableLine / ResetNodes)
/// or a nodeId (Socket / Unsocket / ActivateNode); <see cref="ItemId"/> is the factor to socket;
/// <see cref="CurrentItemId"/> is the factor being removed (Unsocket, toast lookup).</summary>
internal sealed record DeepSlumberOp(DeepSlumberOpKind Kind, int Key, int ItemId, int CurrentItemId)
{
    public static DeepSlumberOp EnableLine(int areaId) => new(DeepSlumberOpKind.EnableLine, areaId, 0, 0);
    // Reset every anchor + factor of one area (whole-area reset — the game has no per-node removal).
    public static DeepSlumberOp ResetNodes(int areaId) => new(DeepSlumberOpKind.ResetNodes, areaId, 0, 0);
    // Activate one normal node ("Anchor of the Mind") in the currently-active area.
    public static DeepSlumberOp ActivateNode(int nodeId) => new(DeepSlumberOpKind.ActivateNode, nodeId, 0, 0);
    public static DeepSlumberOp Socket(int nodeId, int itemId) => new(DeepSlumberOpKind.SocketFactor, nodeId, itemId, 0);
    public static DeepSlumberOp Unsocket(int nodeId, int currentItemId) => new(DeepSlumberOpKind.UnsocketFactor, nodeId, 0, currentItemId);
}
