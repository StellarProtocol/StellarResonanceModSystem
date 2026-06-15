namespace Stellar.Infrastructure.Game.Capture;

internal readonly struct CaptureFrame
{
    public CaptureFrame(string direction, byte[] bytes, string connId)
    {
        Direction = direction;
        Bytes = bytes;
        ConnId = connId;
    }

    public string Direction { get; }
    public byte[] Bytes { get; }
    public string ConnId { get; }
}
