using Stellar.Wire;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

/// <summary>
/// Unit tests for <see cref="EnterSceneReader.TryReadSceneId"/> /
/// <see cref="EnterSceneReader.TryReadSceneAttrs"/> — the enter-scene parse that
/// sources the STABLE per-run scene instance id from
/// <c>EnterScene.EnterSceneInfo(1).SceneAttrs(1) → AttrSceneUuid(342)</c>.
/// Payloads are hand-built protobuf bytes via <see cref="WireBytes"/>; no IL2CPP
/// / BepInEx / Unity dependencies.
///
/// Wire shape exercised:
/// <code>
///   EnterScene       { EnterSceneInfo EnterSceneInfo = 1 }
///   EnterSceneInfo   { AttrCollection SceneAttrs = 1; Entity PlayerEnt = 2;
///                      string SceneGuid = 3; ... }
///   AttrCollection   { int64 Uuid = 1; repeated Attr Attrs = 2 }
///   Attr             { int32 Id = 1; bytes RawData = 2 }   // Id=342 → AttrSceneUuid
/// </code>
/// </summary>
public sealed class EnterSceneReaderTests
{
    private const long ExpectedSceneUuid = 5928374651029L;
    private const long SceneBasicId = 1411L;   // dungeon TEMPLATE id (AttrSceneBasicId)

    [Fact]
    public void TryReadSceneId_ReadsAttrSceneUuid_FromSceneAttrs()
    {
        var body = BuildEnterScene(
            sceneUuid: ExpectedSceneUuid,
            sceneBasicId: SceneBasicId,
            sceneGuid: "instance-guid-abc",
            playerUuid: 0x77L);

        Assert.True(EnterSceneReader.TryReadSceneId(body, out var sceneUuid));
        Assert.Equal(ExpectedSceneUuid, sceneUuid);
    }

    [Fact]
    public void TryReadSceneAttrs_SurfacesGuidAndAllAttrs()
    {
        var body = BuildEnterScene(
            sceneUuid: ExpectedSceneUuid,
            sceneBasicId: SceneBasicId,
            sceneGuid: "instance-guid-abc",
            playerUuid: 0x77L);

        Assert.True(EnterSceneReader.TryReadSceneAttrs(body, out var attrs, out var guid));
        Assert.Equal("instance-guid-abc", guid);
        Assert.Equal(2, attrs.Items.Count);

        // AttrSceneBasicId(341) — the dungeon TEMPLATE, NOT a run id.
        var basic = attrs.Items[0];
        Assert.Equal(AttrTypeIds.AttrSceneBasicId, basic.Id);
        Assert.Equal(SceneBasicId, basic.DecodedLong);

        // AttrSceneUuid(342) — the stable per-run id.
        var uuid = attrs.Items[1];
        Assert.Equal(AttrTypeIds.AttrSceneUuid, uuid.Id);
        Assert.Equal(ExpectedSceneUuid, uuid.DecodedLong);
    }

    [Fact]
    public void TryReadSceneId_NoSceneUuidAttr_ReturnsFalse()
    {
        // SceneAttrs present but carries only AttrSceneBasicId (template), no run id.
        var sceneAttrs = new WireBytes()
            .Tag(2, 2).LengthDelimited(Attr(AttrTypeIds.AttrSceneBasicId, SceneBasicId))
            .ToArray();
        var enterSceneInfo = new WireBytes()
            .Tag(1, 2).LengthDelimited(sceneAttrs)
            .ToArray();
        var body = new WireBytes()
            .Tag(1, 2).LengthDelimited(enterSceneInfo)
            .ToArray();

        Assert.False(EnterSceneReader.TryReadSceneId(body, out var sceneUuid));
        Assert.Equal(0L, sceneUuid);
    }

    [Fact]
    public void TryReadSceneId_ZeroSceneUuid_ReturnsFalse()
    {
        // An explicit AttrSceneUuid=0 must be rejected (0 = "no run").
        var sceneAttrs = new WireBytes()
            .Tag(2, 2).LengthDelimited(Attr(AttrTypeIds.AttrSceneUuid, 0L))
            .ToArray();
        var enterSceneInfo = new WireBytes()
            .Tag(1, 2).LengthDelimited(sceneAttrs)
            .ToArray();
        var body = new WireBytes()
            .Tag(1, 2).LengthDelimited(enterSceneInfo)
            .ToArray();

        Assert.False(EnterSceneReader.TryReadSceneId(body, out var sceneUuid));
        Assert.Equal(0L, sceneUuid);
    }

    [Fact]
    public void TryReadSceneId_NoSceneAttrsSubMessage_ReturnsFalse()
    {
        // EnterSceneInfo with only PlayerEnt(2) — no SceneAttrs(1).
        var playerEnt = new WireBytes().Tag(1, 0).Varint(0x77UL).ToArray();
        var enterSceneInfo = new WireBytes()
            .Tag(2, 2).LengthDelimited(playerEnt)
            .ToArray();
        var body = new WireBytes()
            .Tag(1, 2).LengthDelimited(enterSceneInfo)
            .ToArray();

        Assert.False(EnterSceneReader.TryReadSceneId(body, out _));
    }

    [Fact]
    public void TryReadSceneId_EmptyPayload_ReturnsFalse()
        => Assert.False(EnterSceneReader.TryReadSceneId(System.Array.Empty<byte>(), out _));

    [Fact]
    public void TryReadPlayerEntity_StillReadsPlayerEnt_WithSceneAttrsPresent()
    {
        // Regression: adding the SceneAttrs(1) sibling must not break the existing
        // PlayerEnt(2) navigation that feeds self's loadout.
        var body = BuildEnterScene(
            sceneUuid: ExpectedSceneUuid,
            sceneBasicId: SceneBasicId,
            sceneGuid: "instance-guid-abc",
            playerUuid: 0xC0FFEEL);

        Assert.True(EnterSceneReader.TryReadPlayerEntity(body, out var self));
        Assert.Equal(0xC0FFEEL, self.Uuid);
    }

    // ── fixture builders ────────────────────────────────────────────────────

    // Attr { id(1)=attrId, raw_data(2)=varint(value) }. AttrSceneUuid stores its
    // int64 value as a bare varint in RawData (same as AttrFightPoint / AttrHp).
    private static byte[] Attr(int attrId, long value)
    {
        var raw = new WireBytes().Varint((ulong)value).ToArray();
        return new WireBytes()
            .Tag(1, 0).Varint((ulong)attrId)
            .Tag(2, 2).LengthDelimited(raw)
            .ToArray();
    }

    // EnterScene { EnterSceneInfo(1) { SceneAttrs(1)=AttrCollection{AttrSceneBasicId, AttrSceneUuid},
    //                                  PlayerEnt(2)=Entity{uuid}, SceneGuid(3)=string } }
    private static byte[] BuildEnterScene(long sceneUuid, long sceneBasicId, string sceneGuid, long playerUuid)
    {
        var sceneAttrs = new WireBytes()
            .Tag(1, 0).Varint(0UL)                                          // AttrCollection.Uuid
            .Tag(2, 2).LengthDelimited(Attr(AttrTypeIds.AttrSceneBasicId, sceneBasicId))
            .Tag(2, 2).LengthDelimited(Attr(AttrTypeIds.AttrSceneUuid, sceneUuid))
            .ToArray();

        var playerEnt = new WireBytes()
            .Tag(1, 0).Varint((ulong)playerUuid)                            // Entity.uuid
            .Tag(2, 0).Varint(1UL)                                          // ent_type (skipped)
            .ToArray();

        var enterSceneInfo = new WireBytes()
            .Tag(1, 2).LengthDelimited(sceneAttrs)                          // SceneAttrs
            .Tag(2, 2).LengthDelimited(playerEnt)                           // PlayerEnt
            .Tag(3, 2).String(sceneGuid)                                    // SceneGuid
            .ToArray();

        return new WireBytes()
            .Tag(1, 2).LengthDelimited(enterSceneInfo)                      // EnterScene.EnterSceneInfo
            .ToArray();
    }
}
