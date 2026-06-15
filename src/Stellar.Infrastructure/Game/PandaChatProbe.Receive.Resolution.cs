using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Concrete-type resolution helpers for <see cref="PandaChatProbe"/>'s receive
/// path: chat-shape filter (<see cref="IsChatReceiveType"/>), channel
/// projection (<see cref="MapReceiveType"/>), and IL2CPP class-name lookup
/// (<see cref="ResolveIl2CppConcreteType"/>).
/// </summary>
internal sealed partial class PandaChatProbe
{
    private static bool IsChatReceiveType(string fullName)
    {
        // Real in-world chat is Zproto.*ChitChat* (per proto refs at (local reference)).
        // Chat.Pb.*Receive is the alternate social-chat backend; kept for completeness.
        if (fullName.IndexOf("ChitChat", StringComparison.Ordinal) >= 0) return true;
        return fullName == "Chat.Pb.PrivateChatReceive"
            || fullName == "Chat.Pb.ChannelChatReceive"
            || fullName == "Chat.Pb.WorldChatReceive";
    }

    private static ChatChannel MapReceiveType(string fullName) => fullName switch
    {
        "Chat.Pb.PrivateChatReceive" => ChatChannel.Whisper,
        "Chat.Pb.WorldChatReceive"   => ChatChannel.World,
        "Chat.Pb.ChannelChatReceive" => ChatChannel.Say,
        // Zproto in-world chat carries channel_type on the inner request — refined per-message in OnChatReceiveMessage.
        _ => ChatChannel.Unknown,
    };

    /// <summary>
    /// Recover the concrete IL2CPP class FullName for a boxed managed reference whose
    /// declared type is just an interface (e.g. Google.Protobuf.IBufferMessage).
    /// Reads il2cpp_object_get_class -> il2cpp_class_get_namespace/_name via the
    /// native API. Returns null if the object isn't IL2CPP-backed or the lookup fails.
    /// </summary>
    private static string? ResolveIl2CppConcreteType(object boxedMsg)
    {
        try
        {
            if (boxedMsg is not Il2CppObjectBase obj)
            {
                return null;
            }
            var instancePtr = obj.Pointer;
            if (instancePtr == IntPtr.Zero) return null;

            var classPtr = IL2CPP.il2cpp_object_get_class(instancePtr);
            if (classPtr == IntPtr.Zero) return null;

            var nsPtr = IL2CPP.il2cpp_class_get_namespace(classPtr);
            var namePtr = IL2CPP.il2cpp_class_get_name(classPtr);
            var name = Marshal.PtrToStringAnsi(namePtr);
            if (string.IsNullOrEmpty(name)) return null;
            var ns = Marshal.PtrToStringAnsi(nsPtr);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
        catch
        {
            return null;
        }
    }
}
