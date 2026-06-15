; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
STELLAR0001 | StellarSize | Warning | Method body > 100 LoC
STELLAR0002 | StellarSize | Warning | Method body > 50 LoC
STELLAR0003 | StellarSize | Warning | Method has > 5 parameters
STELLAR0004 | StellarSize | Warning | Constructor has > 6 dependencies
STELLAR0005 | StellarSize | Warning | Interface declares > 8 members
