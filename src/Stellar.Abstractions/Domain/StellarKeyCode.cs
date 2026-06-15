namespace Stellar.Abstractions.Domain;

/// <summary>
/// Subset of UnityEngine.KeyCode values. Integer values match UnityEngine.KeyCode
/// exactly so Infrastructure can cast in either direction. Abstractions cannot
/// reference UnityEngine, so this mirror lives in plain BCL.
/// </summary>
public enum StellarKeyCode
{
    /// <summary>No key / unbound.</summary>
    None = 0,

    // Editing
    /// <summary>Backspace key.</summary>
    Backspace = 8,
    /// <summary>Tab key.</summary>
    Tab = 9,
    /// <summary>Return / Enter key.</summary>
    Return = 13,
    /// <summary>Escape key.</summary>
    Escape = 27,
    /// <summary>Space bar.</summary>
    Space = 32,
    /// <summary>Delete (forward-delete) key.</summary>
    Delete = 127,

    // Punctuation
    /// <summary>Back-quote / grave accent key (`)</summary>
    BackQuote = 96,

    // Digits
    /// <summary>Top-row digit 0.</summary>
    Alpha0 = 48,
    /// <summary>Top-row digit 1.</summary>
    Alpha1 = 49,
    /// <summary>Top-row digit 2.</summary>
    Alpha2 = 50,
    /// <summary>Top-row digit 3.</summary>
    Alpha3 = 51,
    /// <summary>Top-row digit 4.</summary>
    Alpha4 = 52,
    /// <summary>Top-row digit 5.</summary>
    Alpha5 = 53,
    /// <summary>Top-row digit 6.</summary>
    Alpha6 = 54,
    /// <summary>Top-row digit 7.</summary>
    Alpha7 = 55,
    /// <summary>Top-row digit 8.</summary>
    Alpha8 = 56,
    /// <summary>Top-row digit 9.</summary>
    Alpha9 = 57,

    // Letters (ASCII codes match UnityEngine.KeyCode)
    /// <summary>Letter A.</summary>
    A = 97,
    /// <summary>Letter B.</summary>
    B = 98,
    /// <summary>Letter C.</summary>
    C = 99,
    /// <summary>Letter D.</summary>
    D = 100,
    /// <summary>Letter E.</summary>
    E = 101,
    /// <summary>Letter F.</summary>
    F = 102,
    /// <summary>Letter G.</summary>
    G = 103,
    /// <summary>Letter H.</summary>
    H = 104,
    /// <summary>Letter I.</summary>
    I = 105,
    /// <summary>Letter J.</summary>
    J = 106,
    /// <summary>Letter K.</summary>
    K = 107,
    /// <summary>Letter L.</summary>
    L = 108,
    /// <summary>Letter M.</summary>
    M = 109,
    /// <summary>Letter N.</summary>
    N = 110,
    /// <summary>Letter O.</summary>
    O = 111,
    /// <summary>Letter P.</summary>
    P = 112,
    /// <summary>Letter Q.</summary>
    Q = 113,
    /// <summary>Letter R.</summary>
    R = 114,
    /// <summary>Letter S.</summary>
    S = 115,
    /// <summary>Letter T.</summary>
    T = 116,
    /// <summary>Letter U.</summary>
    U = 117,
    /// <summary>Letter V.</summary>
    V = 118,
    /// <summary>Letter W.</summary>
    W = 119,
    /// <summary>Letter X.</summary>
    X = 120,
    /// <summary>Letter Y.</summary>
    Y = 121,
    /// <summary>Letter Z.</summary>
    Z = 122,

    // Navigation
    /// <summary>Up arrow key.</summary>
    UpArrow = 273,
    /// <summary>Down arrow key.</summary>
    DownArrow = 274,
    /// <summary>Right arrow key.</summary>
    RightArrow = 275,
    /// <summary>Left arrow key.</summary>
    LeftArrow = 276,
    /// <summary>Insert key.</summary>
    Insert = 277,
    /// <summary>Home key.</summary>
    Home = 278,
    /// <summary>End key.</summary>
    End = 279,
    /// <summary>Page Up key.</summary>
    PageUp = 280,
    /// <summary>Page Down key.</summary>
    PageDown = 281,

    // Function keys
    /// <summary>F1 function key.</summary>
    F1 = 282,
    /// <summary>F2 function key.</summary>
    F2 = 283,
    /// <summary>F3 function key.</summary>
    F3 = 284,
    /// <summary>F4 function key.</summary>
    F4 = 285,
    /// <summary>F5 function key.</summary>
    F5 = 286,
    /// <summary>F6 function key.</summary>
    F6 = 287,
    /// <summary>F7 function key.</summary>
    F7 = 288,
    /// <summary>F8 function key.</summary>
    F8 = 289,
    /// <summary>F9 function key.</summary>
    F9 = 290,
    /// <summary>F10 function key.</summary>
    F10 = 291,
    /// <summary>F11 function key.</summary>
    F11 = 292,
    /// <summary>F12 function key.</summary>
    F12 = 293,
    /// <summary>F13 function key.</summary>
    F13 = 294,
    /// <summary>F14 function key.</summary>
    F14 = 295,
    /// <summary>F15 function key.</summary>
    F15 = 296,

    // Numpad
    /// <summary>Numpad 0.</summary>
    Keypad0 = 256,
    /// <summary>Numpad 1.</summary>
    Keypad1 = 257,
    /// <summary>Numpad 2.</summary>
    Keypad2 = 258,
    /// <summary>Numpad 3.</summary>
    Keypad3 = 259,
    /// <summary>Numpad 4.</summary>
    Keypad4 = 260,
    /// <summary>Numpad 5.</summary>
    Keypad5 = 261,
    /// <summary>Numpad 6.</summary>
    Keypad6 = 262,
    /// <summary>Numpad 7.</summary>
    Keypad7 = 263,
    /// <summary>Numpad 8.</summary>
    Keypad8 = 264,
    /// <summary>Numpad 9.</summary>
    Keypad9 = 265,
    /// <summary>Numpad decimal point.</summary>
    KeypadPeriod = 266,
    /// <summary>Numpad divide (/).</summary>
    KeypadDivide = 267,
    /// <summary>Numpad multiply (*).</summary>
    KeypadMultiply = 268,
    /// <summary>Numpad minus (-).</summary>
    KeypadMinus = 269,
    /// <summary>Numpad plus (+).</summary>
    KeypadPlus = 270,
    /// <summary>Numpad Enter key.</summary>
    KeypadEnter = 271,
}
