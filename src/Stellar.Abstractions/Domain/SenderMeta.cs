namespace Stellar.Abstractions.Domain;

/// <summary>Optional sender metadata; null on <see cref="ChatMessage"/> when the probe could not resolve.
/// <para><c>Gender</c> is the raw wire enum (typically 1=Male 2=Female); 0 if unknown.
/// <c>Level</c> is the character level; 0 if unknown. <c>IsNewbie</c> mirrors the wire flag.</para></summary>
public readonly record struct SenderMeta(int Level, string? Job, string? Guild, int Gender = 0, bool IsNewbie = false);
