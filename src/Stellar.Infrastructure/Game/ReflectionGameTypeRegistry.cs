using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Locates game types by full name across the loaded assemblies. Both outcomes are MEMOIZED (Photo Studio fw fix
/// round, perf review): a full assembly walk per call was paid by every per-call probe, and a type a game patch
/// renamed/removed was re-walked forever. A negative is forgotten whenever an assembly loads — the only way a
/// missing type can appear — and again when hot-update becomes ready (<see cref="MarkHotUpdateReady"/>); a
/// positive never changes. Thread-safe (the AssemblyLoad handler runs on the loading thread).
/// </summary>
internal sealed class ReflectionGameTypeRegistry : IGameTypeRegistry
{
    private readonly Func<string, Type?> _resolve;
    private readonly ConcurrentDictionary<string, Type?> _memo = new(StringComparer.Ordinal);
    private volatile bool _hotUpdateReady;
    private int _generation;   // bumped by every ForgetNegatives, so a scan racing an assembly load never re-latches a null

    /// <summary>Production registry: scans the AppDomain and forgets negatives on every assembly load.</summary>
    public ReflectionGameTypeRegistry() : this(ScanLoadedAssemblies)
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, _) => ForgetNegatives();
    }

    /// <summary>Test seam: resolves through <paramref name="resolve"/> and subscribes to nothing.</summary>
    internal ReflectionGameTypeRegistry(Func<string, Type?> resolve) => _resolve = resolve;

    /// <summary>True once every hot-update assembly has loaded: a type still missing now will not appear.</summary>
    public bool IsHotUpdateReady => _hotUpdateReady;

    public Type? FindType(string fullName)
    {
        if (_memo.TryGetValue(fullName, out var cached)) return cached;
        var generation = Volatile.Read(ref _generation);
        var type = _resolve(fullName);
        // Always write a positive unconditionally — GetOrAdd would return a racing thread's not-yet-removed
        // null instead of overwriting it, handing back a false negative for a type we just found.
        if (type is not null) { _memo[fullName] = type; return type; }
        _memo.TryAdd(fullName, null);
        // An assembly loaded while we scanned: this null may already be stale — don't keep it.
        if (Volatile.Read(ref _generation) != generation) RemoveNegative(fullName);
        return null;
    }

    /// <summary>Called once from the host's hot-update-ready hook.</summary>
    public void MarkHotUpdateReady()
    {
        _hotUpdateReady = true;
        ForgetNegatives();
    }

    /// <summary>Drops every memoized null so the next lookup re-resolves; positives are kept.</summary>
    internal void ForgetNegatives()
    {
        Interlocked.Increment(ref _generation);
        foreach (var kv in _memo)
            if (kv.Value is null) RemoveNegative(kv.Key);
    }

    // Removes the entry only while it is still null — never drops a racing positive.
    private void RemoveNegative(string fullName) =>
        ((ICollection<KeyValuePair<string, Type?>>)_memo).Remove(new KeyValuePair<string, Type?>(fullName, null));

    private static Type? ScanLoadedAssemblies(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null) return type;
        }
        return null;
    }
}
