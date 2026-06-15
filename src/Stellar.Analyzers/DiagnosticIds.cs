using Microsoft.CodeAnalysis;

namespace Stellar.Analyzers;

internal static class DiagnosticIds
{
    internal const string MethodTooLongBlocker = "STELLAR0001"; // > 100 LoC
    internal const string MethodTooLongMajor   = "STELLAR0002"; // > 50 LoC
    internal const string TooManyParameters    = "STELLAR0003"; // > 5 params
    internal const string TooManyCtorDeps       = "STELLAR0004"; // > 6 ctor params
    internal const string InterfaceTooWide      = "STELLAR0005"; // > 8 members

    internal const string Category = "StellarSize";

    // Default severity is Warning; .editorconfig is the single knob that
    // ratchets these to Error in P4 (Task 17).
    internal static DiagnosticDescriptor Make(string id, string title, string messageFormat) =>
        new(id, title, messageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);
}
