using System.Collections.Generic;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The exact interop signatures the <see cref="EcsPlayDetours"/> delegates mirror — a detour goes live only on an
/// interop method carrying one of these (patch-safety review 2026-10-02). Apart from the detours so reading them never
/// loads the interop runtime (unit-tested).</summary>
internal static class EcsPlaySignatures
{
    /// <summary>The release_3.7 interop signature each <see cref="EcsPlayDetours"/> delegate mirrors (struct arguments: <c>ExternalBlobPtr</c> = one
    /// pointer = <c>nint</c>; by-value <c>float2</c> = 8 bytes in one register = <c>long</c>; <c>in float2</c> = <c>nint</c>).</summary>
    internal static readonly IReadOnlyDictionary<string, NativeSignature> Expected = new Dictionary<string, NativeSignature>
    {
        ["PlayState"] = new("System.UInt32", new[]
        {
            "System.UInt32", "System.UInt16", "System.UInt32", "Unity.Mathematics.float2&", "System.Single", "System.Single",
            "System.Single", "System.Single", "System.Int32", "System.Single",
        }),
        ["PlayClip"] = new("System.UInt32", new[]
        {
            "System.UInt32", "System.UInt16", "ECSModel.ExternalBlobPtr`1[ECSModel.AnimationClipBlob]", "System.Single",
            "System.Single", "System.Single", "System.Single", "System.Int32", "System.Single",
        }),
        ["PlayDynamicState"] = new("System.UInt32", new[]
        {
            "System.UInt32", "System.UInt16", "ECSModel.ExternalBlobPtr`1[ECSModel.StateBlob]", "Unity.Mathematics.float2",
            "System.Single", "System.Single", "System.Single", "System.Single", "System.Single",
        }),
    };
}
