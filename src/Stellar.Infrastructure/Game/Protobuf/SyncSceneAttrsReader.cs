using System;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Parser for <c>WorldNtf.SyncSceneAttrs</c> (method 7) — the SCENE/World attribute sync.
/// <code>
///   message SyncSceneAttrs { AttrCollection attrs = 1; }
/// </code>
/// This is the update carrier for the same scene-level attr collection that arrives on zone-in as
/// <c>EnterSceneInfo.SceneAttrs</c> (read by <see cref="EnterSceneReader.TryReadSceneAttrs"/>) — the
/// game applies it via <c>Panda.ZGame.ZWorld.ParseAttrProto(AttrCollection)</c> into the world attr
/// collection its own UI then reads back with <c>Z.World:GetWorldLuaAttr(id)</c>.
///
/// <para>Shape confirmed against the generated message in both dumps: the Cpp2IL
/// <c>Zproto.WorldNtf.Types.SyncSceneAttrs</c> has exactly one member (<c>AttrCollection Attrs</c>),
/// and the third-party generated parser writes/reads it at raw tag 10 — field 1, wire-type 2.</para>
///
/// <para>Defensive like every sibling reader: any malformed field returns <see langword="false"/>
/// with no partial state leaking out.</para>
/// </summary>
internal static class SyncSceneAttrsReader
{
    private const int AttrsField = 1;

    public static bool TryRead(ReadOnlyMemory<byte> payload, out AttrCollectionMsg attrs)
    {
        attrs = default;
        var span = payload.Span;
        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire)) return false;
            if (field == AttrsField && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(span, ref pos, out var inner)) return false;
                // pos already sits past the sub-message, so its slice starts at (pos - length) —
                // same zero-copy convention AttrCollectionReader documents for its own nesting.
                return AttrCollectionReader.TryRead(payload.Slice(pos - inner.Length, inner.Length), out attrs);
            }
            if (!WireProtocol.SkipField(span, ref pos, wire)) return false;
        }
        return false;
    }
}
