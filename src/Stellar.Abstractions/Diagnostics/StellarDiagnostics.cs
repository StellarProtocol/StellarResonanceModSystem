namespace Stellar.Abstractions.Diagnostics;

/// <summary>
/// Diagnostic mode toggle. Set environment variable
/// <c>STELLAR_DIAGNOSTICS=1</c> before launching the game (in
/// Heroic's env-var settings) to enable per-event logs in
/// <c>PandaChatProbe</c>, <c>PandaWireTap</c>,
/// <c>PandaCombatStubProbe</c>, and <c>ChatService</c>. Default
/// off so normal play produces minimal log volume.
///
/// <para>
/// The value is read once at process start and cached for the
/// session — flipping the env var mid-run has no effect. This keeps
/// the hot-path check a single field read (no repeated env lookups).
/// </para>
///
/// <para>
/// Lives in <c>Stellar.Abstractions</c> so every layer
/// (Application, Infrastructure, Host) can gate on it without
/// crossing the layered-dependency boundary.
/// </para>
/// </summary>
public static class StellarDiagnostics
{
    private static readonly bool _enabled = ResolveEnabled();

    /// <summary>
    /// <c>true</c> when <c>STELLAR_DIAGNOSTICS=1</c> (or
    /// <c>=true</c>, case-insensitive) was set in the process
    /// environment at startup. <c>false</c> otherwise.
    /// </summary>
    public static bool IsEnabled => _enabled;

    // STELLAR_DIAGNOSTICS=1 in the env OR "DIAGNOSTICS" in game_mini/stellar_perf.flags (the deploy-script mode
    // file: `install-stellar.sh test` writes it; prod/perf omit it). One switch, no Heroic env edits.
    private static bool ResolveEnabled() => PerfControls.Flag("DIAGNOSTICS");
}
