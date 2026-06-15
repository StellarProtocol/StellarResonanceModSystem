using System.Collections.Generic;
using System.Text;

namespace Stellar.Infrastructure.Game.Capture;

internal static class ProtoJson
{
    public static string Node(ProtoNode n)
    {
        var sb = new StringBuilder("{");
        sb.Append("\"truncated\":").Append(n.Truncated ? "true" : "false").Append(",\"fields\":[");
        for (int i = 0; i < n.Fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            Field(sb, n.Fields[i]);
        }
        return sb.Append("]}").ToString();
    }

    private static void Field(StringBuilder sb, ProtoField f)
    {
        sb.Append("{\"f\":").Append(f.FieldNumber).Append(",\"kind\":\"").Append(f.Kind).Append('"');
        switch (f.Kind)
        {
            case ProtoKind.Varint:
                sb.Append(",\"v\":").Append(f.VarintValue);
                break;
            case ProtoKind.Fixed32:
            case ProtoKind.Fixed64:
                sb.Append(",\"v\":").Append(f.FixedValue);
                break;
            case ProtoKind.String:
                sb.Append(",\"s\":").Append(Quote(f.StringValue));
                break;
            case ProtoKind.Bytes:
                sb.Append(",\"len\":").Append(f.ByteLength);
                break;
            case ProtoKind.Message:
                sb.Append(",\"msg\":").Append(Node(f.Message!));
                break;
        }
        sb.Append('}');
    }

    public static string Typed(TypedReaderRegistry.TypedResult t)
    {
        var sb = new StringBuilder("{\"type\":").Append(Quote(t.TypeName)).Append(",\"fields\":{");
        bool first = true;
        foreach (var kv in t.Fields)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Quote(kv.Key)).Append(':').Append(Scalar(kv.Value));
        }
        return sb.Append("}}").ToString();
    }

    private static string Scalar(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => Quote(s),
        System.Collections.IEnumerable e => List(e),
        _ => v.ToString() ?? "null",
    };

    private static string List(System.Collections.IEnumerable e)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var item in e)
        {
            if (!first) sb.Append(',');
            first = false;
            if (item is IDictionary<string, object?> d) sb.Append(Dict(d));
            else sb.Append(Scalar(item));
        }
        return sb.Append(']').ToString();
    }

    private static string Dict(IDictionary<string, object?> d)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in d)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Quote(kv.Key)).Append(':').Append(Scalar(kv.Value));
        }
        return sb.Append('}').ToString();
    }

    private static string Quote(string? s)
    {
        if (s is null) return "null";
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            _ = c switch
            {
                '"'  => sb.Append("\\\""),
                '\\' => sb.Append("\\\\"),
                '\n' => sb.Append("\\n"),
                '\r' => sb.Append("\\r"),
                '\t' => sb.Append("\\t"),
                < ' ' => sb.Append($"\\u{(int)c:x4}"),
                _ => sb.Append(c),
            };
        }
        return sb.Append('"').ToString();
    }
}
