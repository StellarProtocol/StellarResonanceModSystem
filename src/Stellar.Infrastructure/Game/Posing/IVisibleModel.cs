namespace Stellar.Infrastructure.Game.Posing;

/// <summary>A posed model that stands in for a person on screen (a photo copy or an NPC stand-in): the game model the
/// player sees instead of the real person — what a per-person key light / rim must be drawn on.</summary>
internal interface IVisibleModel
{
    /// <summary>The live game model (<c>Panda.ZGame.ZModel</c>); null while loading or once removed.</summary>
    object? VisibleModel { get; }
}
