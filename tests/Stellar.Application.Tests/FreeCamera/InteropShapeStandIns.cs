#pragma warning disable CS8981 // the interop type is lower-case (Unity.Mathematics.float2)
// Stand-ins carrying the EXACT full names of the release_3.7 interop types in the ECS play writers' and ApplyAllData's
// signatures, so a fake method in a test has the same signature text (NativeSignature.TypeText) as the real interop method.
// Only their names matter (patch-safety review 2026-10-02: the native detours go live on the full exact signature).

namespace ECSModel
{
    internal struct ExternalBlobPtr<T> { }

    internal struct AnimationClipBlob { }

    internal struct StateBlob { }
}

namespace Unity.Mathematics
{
    internal struct float2 { }
}

namespace Panda.Utility.Quality
{
    internal struct QualityData { }
}
