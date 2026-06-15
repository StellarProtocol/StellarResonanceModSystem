namespace Stellar.Abstractions.Services;

/// <summary>Plugin-scoped logger. Output is routed through the host's log sink.</summary>
public interface IPluginLog
{
    /// <summary>Logs an informational message to the BepInEx log.</summary>
    void Info(string message);
    /// <summary>Logs a warning message to the BepInEx log.</summary>
    void Warning(string message);
    /// <summary>Logs an error message to the BepInEx log.</summary>
    void Error(string message);
    /// <summary>Logs a debug-level message to the BepInEx log (visible only at Debug log level).</summary>
    void Debug(string message);
}
