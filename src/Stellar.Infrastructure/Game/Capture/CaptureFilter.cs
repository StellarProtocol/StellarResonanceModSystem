using System;
using System.Collections.Generic;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game.Capture;

/// <summary>Parses STELLAR_WIRECAP into a frame predicate. Pure; no IO.</summary>
internal sealed class CaptureFilter
{
    private readonly bool _all;
    private readonly List<Term> _terms;

    public bool Enabled { get; }
    public string? Error { get; }

    private CaptureFilter(bool enabled, bool all, List<Term> terms, string? error)
    {
        Enabled = enabled;
        _all = all;
        _terms = terms;
        Error = error;
    }

    public static CaptureFilter Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return new CaptureFilter(false, false, new(), null);

        spec = spec.Trim();

        if (spec == "all")
            return new CaptureFilter(true, true, new(), null);

        if (spec == "team")
            return Team();

        var terms = new List<Term>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseTerm(raw, out var t, out var err))
                return new CaptureFilter(false, false, new(), $"bad term '{raw}': {err}");
            terms.Add(t);
        }

        if (terms.Count == 0)
            return new CaptureFilter(false, false, new(), "no terms");

        return new CaptureFilter(true, false, terms, null);
    }

    private static CaptureFilter Team()
    {
        var terms = new List<Term>
        {
            new(966773353, null, null),
            new(103198054, null, WireMessageKind.Return),
        };
        return new CaptureFilter(true, false, terms, null);
    }

    public bool Allows(ulong svc, uint method, WireMessageKind kind)
    {
        if (!Enabled) return false;
        if (_all) return true;
        foreach (var t in _terms)
            if (t.Matches(svc, kind)) return true;
        return false;
    }

    private static bool TryParseTerm(string raw, out Term term, out string? err)
    {
        term = default;
        err = null;
        var parts = raw.Split(':');

        if (parts[0] == "kind" && parts.Length == 2)
        {
            if (!Enum.TryParse<WireMessageKind>(parts[1], true, out var k))
            {
                err = "unknown kind";
                return false;
            }
            term = new Term(null, null, k);
            return true;
        }

        if (parts[0] == "svc" && parts.Length is 2 or 3)
        {
            if (!ulong.TryParse(parts[1], out var svc))
            {
                err = "svc not a number";
                return false;
            }
            WireMessageKind? kind = null;
            if (parts.Length == 3)
            {
                if (!Enum.TryParse<WireMessageKind>(parts[2], true, out var k))
                {
                    err = "unknown kind";
                    return false;
                }
                kind = k;
            }
            term = new Term(svc, null, kind);
            return true;
        }

        err = "expected svc:<uuid>[:Kind] or kind:<Kind>";
        return false;
    }

    private readonly record struct Term(ulong? Svc, uint? Method, WireMessageKind? Kind)
    {
        public bool Matches(ulong svc, WireMessageKind kind)
        {
            if (Svc.HasValue && Svc.Value != svc) return false;
            if (Kind.HasValue && Kind.Value != kind) return false;
            return true;
        }
    }
}
