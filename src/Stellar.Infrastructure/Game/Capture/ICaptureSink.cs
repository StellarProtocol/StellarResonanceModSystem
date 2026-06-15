using System;

namespace Stellar.Infrastructure.Game.Capture;

internal interface ICaptureSink : IDisposable
{
    void Write(string jsonLine);
    bool Truncated { get; }
}
