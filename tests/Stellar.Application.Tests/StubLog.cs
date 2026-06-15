using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Tests;

/// <summary>In-memory <see cref="IPluginLog"/> for assertions.</summary>
internal sealed class StubLog : IPluginLog
{
    public List<string> InfoLines    { get; } = new();
    public List<string> WarningLines { get; } = new();
    public List<string> ErrorLines   { get; } = new();
    public List<string> DebugLines   { get; } = new();

    public void Info(string message)    => InfoLines.Add(message);
    public void Warning(string message) => WarningLines.Add(message);
    public void Error(string message)   => ErrorLines.Add(message);
    public void Debug(string message)   => DebugLines.Add(message);
}
