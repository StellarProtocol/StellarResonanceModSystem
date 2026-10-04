namespace Stellar.Abstractions.Services;

/// <summary>
/// Holds ReShade uniform variables (effect settings) at fixed values, in the game's ReShade runtime and in the separate
/// runtime photos may be drawn in — whatever preset is loaded, and again after every effect reload or preset switch
/// (ReShade resets uniforms to their shader defaults on each reload). Use it for a setting an effect must never have in
/// this game, for example AcerolaFX's <c>_MaskUI</c>, whose default hides the effect's output here.
/// <para>Needs the Stellar ReShade bridge 1.1.0 or later; with an older bridge every call returns false or does nothing.
/// Overrides set before the bridge is loaded are held and sent when it is. Overrides are not saved by Stellar (ReShade
/// does write the held value into a preset whenever it saves one), and a plugin's overrides are removed when the plugin
/// is unloaded. If two plugins override the same variable, the last call wins. Main thread only.</para>
/// </summary>
public interface IReShadeUniforms
{
    /// <summary>Holds <paramref name="variable"/> of <paramref name="effectFile"/> (the effect file name as ReShade
    /// reports it, e.g. "AcerolaFX_End.fx"; null or empty = that variable in every effect) at <paramref name="value"/>:
    /// 1 to 16 numbers separated by commas ("0", "1,0,0"), or true / false, converted to the variable's type. A null
    /// or empty <paramref name="value"/> removes the override (the variable then keeps its current value until ReShade
    /// next loads a preset or effect). Returns false for an empty variable name, a value that is not in that form, or a
    /// bridge older than 1.1.0; true when the override was sent (or is held until the bridge is loaded).</summary>
    bool SetUniformOverride(string? effectFile, string variable, string? value);

    /// <summary>Removes every override this plugin set.</summary>
    void ClearUniformOverrides();
}
