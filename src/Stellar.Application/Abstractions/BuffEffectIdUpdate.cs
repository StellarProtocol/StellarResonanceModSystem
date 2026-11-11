namespace Stellar.Application.Abstractions;

/// <summary>
/// One decoded change to a buff instance's effect ids (see <c>ICombatBuffEffectLookup</c>), in wire order.
/// <see cref="HostUuid"/> is the uuid the wire named (<c>BuffEffect.HostUuid</c> / <c>BuffInfo.HostUuid</c> /
/// <c>BuffEffectSync.Uuid</c>), 0 when it named none — the sink then uses the entity the message belongs to.
/// <see cref="EffectIds"/> null = the buff was removed (drop the entry); otherwise a fresh, never-empty array that
/// replaces the held one.
/// </summary>
internal readonly record struct BuffEffectIdUpdate(long HostUuid, int BuffUuid, int[]? EffectIds);
