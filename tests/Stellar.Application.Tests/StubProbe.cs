using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests;

/// <summary>
/// In-memory <see cref="IChatProbe"/> stub.
/// Tests drive <see cref="OnMessageReceived"/> to simulate inbound traffic and
/// read <see cref="SendCalls"/> to assert outbound behavior.
/// </summary>
internal sealed class StubProbe : IChatProbe
{
    public Action<ChatMessage>? OnMessageReceived { get; set; }

    public List<(ChatTarget Target, string Text)> SendCalls { get; } = new();

    /// <summary>Value <see cref="TrySend"/> returns. Default true.</summary>
    public bool NextTrySendResult { get; set; } = true;

    /// <summary>Failure reason returned when <see cref="NextTrySendResult"/> is false.</summary>
    public string? NextFailureReason { get; set; }

    /// <summary>When true, <see cref="TrySend"/> throws instead of returning.</summary>
    public bool TrySendShouldThrow { get; set; }

    public bool TrySend(ChatTarget target, string text, out string? failureReason)
    {
        if (TrySendShouldThrow) throw new InvalidOperationException("stub: TrySend forced throw");
        SendCalls.Add((target, text));
        failureReason = NextTrySendResult ? null : NextFailureReason;
        return NextTrySendResult;
    }
}
