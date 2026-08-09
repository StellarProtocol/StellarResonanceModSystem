using Microsoft.CodeAnalysis;

namespace Stellar.Analyzers;

internal static class DiagnosticIds
{
    internal const string MethodTooLongBlocker = "STELLAR0001"; // > 100 LoC
    internal const string MethodTooLongMajor   = "STELLAR0002"; // > 50 LoC
    internal const string TooManyParameters    = "STELLAR0003"; // > 5 params
    internal const string TooManyCtorDeps       = "STELLAR0004"; // > 6 ctor params
    internal const string InterfaceTooWide      = "STELLAR0005"; // > 8 members
    internal const string WorldGatedMissingGuard = "STELLAR0006"; // [WorldGated] method lacks IsWorldActive guard

    internal const string Category = "StellarSize";
    internal const string SafetyCategory = "StellarSafety";

    // Default severity is Warning; .editorconfig is the single knob that
    // ratchets these to Error in P4 (Task 17).
    internal static DiagnosticDescriptor Make(string id, string title, string messageFormat) =>
        new(id, title, messageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    // Safety rule — ships at Error by default (a missing guard corrupts the world-connect for everyone),
    // and is reaffirmed in .editorconfig.
    internal static DiagnosticDescriptor MakeError(string id, string title, string messageFormat) =>
        new(id, title, messageFormat, SafetyCategory, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
