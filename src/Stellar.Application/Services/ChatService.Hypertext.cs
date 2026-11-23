using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Rich-message finishing on the main thread (<see cref="Drain"/>): the wire thread only decodes the raw hypertext
/// slots and voice payload (no game access there); here the hypertext gets its display segments — template + names
/// from the game's live tables, which must be read on the main thread — and the voice transcript is sanitised.
/// Only hypertext/voice messages pay anything (one record copy + a few cached lookups); every other message passes
/// through untouched with zero allocation.
/// </summary>
internal sealed partial class ChatService
{
    private ChatHypertextBuilder _hypertext = new(NullChatNameLookup.Instance);
    private bool _hypertextFailLogged;

    /// <summary>Name source for hypertext segments (Host wiring: <see cref="GameChatNameLookup"/> over IGameData +
    /// the Infrastructure chat-link tables). Unset = fallbacks only.</summary>
    internal void UseNameLookup(IChatNameLookup lookup)
        => _hypertext = new ChatHypertextBuilder(lookup ?? NullChatNameLookup.Instance);

    // Main thread, from Drain.
    private ChatMessage FinishRichContent(ChatMessage msg)
    {
        if (msg.Hypertext is null && msg.Voice is null) return msg;
        try
        {
            var ht = msg.Hypertext is { } raw ? _hypertext.Build(raw, msg.SenderName ?? string.Empty, msg.Text ?? string.Empty) : null;
            var voice = msg.Voice is { } v ? v with { Transcript = ChatRichText.Sanitize(v.Transcript) } : null;
            return msg with { Hypertext = ht, Voice = voice };
        }
        catch (Exception ex)
        {
            // A layout bug must never cost the message: deliver it with the raw slots (Segments empty → Text fallback).
            if (!_hypertextFailLogged)
            {
                _hypertextFailLogged = true;
                _log.Warning($"[Chat] hypertext layout threw (config={msg.Hypertext?.ConfigId}): {ex.GetType().Name}: {ex.Message}");
            }
            return msg;
        }
    }
}
