using System;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface — locates types loaded into the running game process.
/// Used by services that need late-bound access to game classes (e.g. <c>Panda.Core.Game</c>).
/// </summary>
internal interface IGameTypeRegistry
{
    Type? FindType(string fullName);
}
