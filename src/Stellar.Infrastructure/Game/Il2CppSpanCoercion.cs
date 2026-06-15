using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Shared helpers for coercing IL2CPP-projected <c>ReadOnlySpan&lt;byte&gt;</c>
/// wrappers into managed <c>byte[]</c>. The IL2CPP-projected
/// <c>ReadOnlySpan&lt;byte&gt;.get_Item</c> indexer is broken under HarmonyX
/// ref-struct boxing — every read returns the 0x40 buffer sentinel regardless
/// of actual content. <c>ToArray()</c> on the same wrapper dereferences the
/// byref inside the method and returns the real bytes.
///
/// State is process-wide and resolved at most once (via the
/// <see cref="SpanExtractorResolved"/> interlock guard). Exposed
/// <c>internal</c> so every probe that owns an IL2CPP-span call site
/// (<see cref="PandaWireTap"/>, <see cref="PandaChatProbe"/>,
/// <see cref="PandaCombatStubProbe"/>) shares the same resolved extractor.
/// </summary>
internal static class Il2CppSpanCoercion
{
    // Cached extractor for Il2CppSystem.ReadOnlySpan<byte>. Resolved on first
    // packet (I/O thread), then read-only. The reflection lookups stay off the
    // hot path after the first invocation.
    internal static volatile MethodInfo? SpanToArrayMethod;
    internal static volatile bool SpanExtractorReady;
    // 0 = not attempted, 1 = attempted (success or fail — never retry).
    internal static int SpanExtractorResolved;

    /// <summary>
    /// Resolve a usable extractor (ToArray) on the IL2CPP ReadOnlySpan&lt;byte&gt;
    /// wrapper. Writes to the static accessor field. Idempotent — called at
    /// most once via the Interlocked.Exchange guard.
    /// </summary>
    internal static void ResolveSpanExtractor(IPluginLog log, Type t)
    {
        try
        {
            MethodInfo? toArray = null;
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.IsGenericMethodDefinition) continue;
                if (m.Name == "ToArray" && m.GetParameters().Length == 0)
                {
                    toArray = m;
                    break;
                }
            }

            SpanToArrayMethod = toArray;
            SpanExtractorReady = toArray is not null;
            if (toArray is not null)
            {
                log.Info($"[WireTap] resolved ToArray on {t.FullName}");
            }
            else
            {
                log.Warning($"[WireTap] no ToArray() found on {t.FullName}; recv path will degrade");
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[WireTap] span extractor resolution threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Coerce a ToArray() result into a managed <c>byte[]</c>. The IL2CPP
    /// wrapper sometimes returns a managed array directly, and sometimes
    /// returns an <c>Il2CppStructArray&lt;byte&gt;</c> — we handle both.
    /// Returns null if the input shape isn't recognized.
    /// </summary>
    internal static byte[]? CoerceToByteArray(object raw)
    {
        if (raw is byte[] managed) return managed;

        // Fast path: Il2CppStructArray<byte> is a blittable IL2CPP array — bulk
        // CopyTo into a managed array. The reflective per-element walk below boxed
        // every byte (GetValue + Convert.ToByte), which is ~N reflection calls and
        // 2N boxed objects on the hot network-receive path (a ~10 KB container
        // delta = thousands of allocations per packet). The combat probe shares
        // this helper and benefits too. Falls through to the reflective walk if
        // the shape differs or CopyTo fails.
        if (raw is Il2CppStructArray<byte> structArr)
        {
            try
            {
                var n = structArr.Length;
                if (n <= 0) return null;
                var dst = new byte[n];
                structArr.CopyTo(dst, 0);
                return dst;
            }
            catch { /* fall through to the reflective path */ }
        }

        // Il2CppStructArray<byte> exposes Length + indexer. Walk via reflection.
        var t = raw.GetType();
        try
        {
            var lenProp = t.GetProperty("Length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var itemProp = t.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (lenProp is null || itemProp is null) return null;

            var lenObj = lenProp.GetValue(raw);
            if (lenObj is not int len || len <= 0) return null;

            var result = new byte[len];
            var idxArgs = new object?[1];
            for (int i = 0; i < len; i++)
            {
                idxArgs[0] = i;
                var v = itemProp.GetValue(raw, idxArgs);
                if (v is null) return null;
                result[i] = Convert.ToByte(v);
            }
            return result;
        }
        catch
        {
            return null;
        }
    }
}
