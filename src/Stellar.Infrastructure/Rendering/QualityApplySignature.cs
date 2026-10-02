using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Rendering;

/// <summary>The exact interop signature <see cref="QualityApplyDetour"/>'s delegate mirrors — the detour goes live only on a
/// <c>QualityGradeSetting.ApplyAllData</c> carrying it. Apart from the detour so reading it never loads the interop runtime
/// (unit-tested).</summary>
internal static class QualityApplySignature
{
    internal const string Method = "ApplyAllData";

    /// <summary>release_3.7: <c>static void ApplyAllData([In] ref QualityData data, bool excludeFrameRate)</c>.</summary>
    internal static readonly NativeSignature Expected =
        new("System.Void", new[] { "Panda.Utility.Quality.QualityData&", "System.Boolean" });
}
