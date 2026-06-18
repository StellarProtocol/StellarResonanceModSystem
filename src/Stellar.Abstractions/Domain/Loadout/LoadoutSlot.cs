namespace Stellar.Abstractions.Domain.Loadout;

/// <summary>A saved in-game loadout entry: its identifier, display name, and whether it is currently active.</summary>
/// <param name="Index">Stable game-defined identifier passed to <see cref="Stellar.Abstractions.Services.ILoadout.ApplyAsync"/>. This is the game's loadout/project id, not necessarily a positional index; see the loadout recon findings.</param>
/// <param name="Name">Display name as shown in the in-game dropdown (e.g. "Ici-LF"), or a fallback like "Loadout N" if unresolved.</param>
/// <param name="IsCurrent">True if this loadout is the one currently applied.</param>
public sealed record LoadoutSlot(int Index, string Name, bool IsCurrent);
