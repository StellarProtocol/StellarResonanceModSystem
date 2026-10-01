using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Raw keyboard/mouse reads for a shield holder (Unity input, upstream of the game's mask). Main thread.</summary>
internal interface IShieldInputReader
{
    bool IsHeld(StellarKeyCode key);
    ModifierKeys Modifiers { get; }
    bool IsMouseHeld(int button);
    /// <summary>Pointer movement since the previous frame, pixels, origin top-left (sampled once per frame).</summary>
    (float X, float Y) MouseDelta { get; }
    /// <summary>Wheel notches this frame (sampled once per frame).</summary>
    float Wheel { get; }
    (float X, float Y) Pointer { get; }
}
