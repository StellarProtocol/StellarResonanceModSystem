// net6 polyfills for the C# 11 `required` members feature. These attribute types ship in the
// net7+ BCL; on net6 the compiler still emits them for `required`/`init` members, so they must exist
// somewhere in the compilation. Declared `internal` (per-assembly polyfill pattern) so they don't leak
// into the public surface. Do NOT reference these directly — the compiler consumes them implicitly.

namespace System.Runtime.CompilerServices
{
    /// <summary>Polyfill of the net7+ <c>RequiredMemberAttribute</c> (marks a member as compiler-required).</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>Polyfill of the net7+ <c>CompilerFeatureRequiredAttribute</c> (gates a feature, e.g. "RequiredMembers").</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }

        public bool IsOptional { get; init; }

        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Polyfill of the net7+ <c>SetsRequiredMembersAttribute</c>. A record with `required` members emits a
    /// synthesized copy-constructor annotated with this, so the type is unusable on net6 without it.</summary>
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
