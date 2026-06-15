using System;
using System.Collections;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// State-free reflection helpers used by <see cref="PandaChatProbe"/> to read
/// properties and lists off chat-related game objects (StubCall wrappers,
/// Chat.Pb.*ChatReceive protobuf messages, etc.).
///
/// Extracted from <c>PandaChatProbe.Receive.cs</c> in Phase 3c Step 3 so the
/// receive partial stays below the 800-LoC blocker threshold. All methods are
/// state-free: they receive the target instance plus the member descriptor(s)
/// and return scalar values or null on failure. Exceptions are swallowed by
/// design — these helpers run on the network I/O thread and must never throw
/// across the IL2CPP boundary.
/// </summary>
internal static class ChatPropertyReader
{
    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Safe property read with stringification — returns <c>"?"</c> when the
    /// property descriptor is null or the read throws, <c>"null"</c> when the
    /// value is null, otherwise <c>value.ToString()</c>. Used for diagnostic
    /// log lines where formatter exceptions must not interrupt the receive
    /// path.
    /// </summary>
    public static string TryReadProp(object instance, PropertyInfo? prop)
    {
        if (prop is null) return "?";
        try
        {
            var v = prop.GetValue(instance);
            return v?.ToString() ?? "null";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// Resolve the protobuf-generated <c>msg_list</c> repeated-field member on
    /// a chat receive wrapper. Tries PascalCase property first
    /// (<c>MsgList</c>), then the snake-cased backing field
    /// (<c>msgList_</c>/<c>msg_list</c>). Returns null when no member matches.
    /// </summary>
    public static MemberInfo? ResolveListMember(Type t)
    {
        // protobuf-generated PascalCase property; tolerate snake-cased backing too.
        var prop = t.GetProperty("MsgList", AnyInstance)
            ?? t.GetProperty("msgList_", AnyInstance)
            ?? t.GetProperty("msg_list", AnyInstance);
        if (prop is not null) return prop;
        return t.GetField("msgList_", AnyInstance)
            ?? t.GetField("msg_list", AnyInstance);
    }

    /// <summary>
    /// Read a repeated-field member as <see cref="IEnumerable"/>. Returns null
    /// when the member descriptor is null, the read throws, or the value is
    /// not enumerable.
    /// </summary>
    public static IEnumerable? ReadList(object obj, MemberInfo? member)
    {
        if (member is null) return null;
        try
        {
            object? raw = member switch
            {
                PropertyInfo p => p.GetValue(obj),
                FieldInfo f    => f.GetValue(obj),
                _ => null,
            };
            return raw as IEnumerable;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read the first non-empty string-typed property/field whose name matches
    /// any of <paramref name="names"/>. Returns null when no candidate yields
    /// a non-empty value.
    /// </summary>
    public static string? ReadStringMember(object obj, Type t, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var prop = t.GetProperty(name, AnyInstance);
                if (prop is not null && prop.PropertyType == typeof(string))
                {
                    var v = prop.GetValue(obj) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch
            {
                // try next candidate
            }
            try
            {
                var field = t.GetField(name, AnyInstance);
                if (field is not null && field.FieldType == typeof(string))
                {
                    var v = field.GetValue(obj) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch
            {
                // try next candidate
            }
        }
        return null;
    }

    /// <summary>
    /// Read the first integer-typed property/field whose name matches any of
    /// <paramref name="names"/>, widening to <see cref="long"/>. Returns 0
    /// when no candidate matches or the read throws.
    /// </summary>
    public static long ReadInt64Member(object obj, Type t, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var prop = t.GetProperty(name, AnyInstance);
                if (prop is not null)
                {
                    var raw = prop.GetValue(obj);
                    return raw switch
                    {
                        long l => l,
                        int i  => i,
                        uint u => u,
                        ulong ul => unchecked((long)ul),
                        null   => 0L,
                        _ => 0L,
                    };
                }
            }
            catch
            {
                // try next candidate
            }
            try
            {
                var field = t.GetField(name, AnyInstance);
                if (field is not null)
                {
                    var raw = field.GetValue(obj);
                    return raw switch
                    {
                        long l => l,
                        int i  => i,
                        uint u => u,
                        ulong ul => unchecked((long)ul),
                        null   => 0L,
                        _ => 0L,
                    };
                }
            }
            catch
            {
                // try next candidate
            }
        }
        return 0L;
    }
}
