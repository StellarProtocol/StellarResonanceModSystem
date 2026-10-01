using Stellar.Abstractions.Domain;
using UnityEngine;
namespace Stellar.Infrastructure.UI;

/// <summary>Shift / Ctrl / Alt from Unity input (shared by the hotkey gateway and the shield reader).</summary>
internal static class UnityModifiers
{
    public static ModifierKeys Read()
    {
        var m = ModifierKeys.None;
        try
        {
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) m |= ModifierKeys.Shift;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= ModifierKeys.Ctrl;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) m |= ModifierKeys.Alt;
        }
        catch { /* input subsystem not ready during very early boot */ }
        return m;
    }
}
