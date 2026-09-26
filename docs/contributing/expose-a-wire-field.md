# Exposing a wire field to plugins

The game server already sends far more than the framework passes on. Most useful new plugin features
start the same way: find a field the server sends, decode it, and expose it through
`Stellar.Abstractions`. This guide walks through one real change end to end, so you can repeat it for
another field.

The example is from framework 2.11.0. A player's full buff list rides on the `SyncNearEntities` message
when that player comes into range, in `Entity.buff_infos` (field 7). Before 2.11.0 the framework skipped
it and only learned about buffs when they changed. The change decoded field 7 and exposed it as
`CombatEvent.EntityBuffsSeeded`. The relevant commits are `e6c692f`, `ef5aa00` and `58e2a57`.

The site's [wire coverage page](https://docs.stellarresonance.app/wire/) lists which fields are decoded
today and which are still skipped.

![From a wire packet to a plugin event](../diagrams/wire-to-plugin.svg)

The path, left to right:

| Step | Layer | File in the 2.11.0 change |
|---|---|---|
| Hook and route the packet | Infrastructure | `src/Stellar.Infrastructure/Game/PandaCombatStubProbe.cs`, `PandaCombatStubProbe.Receive.cs` |
| Decode the bytes | Infrastructure | `src/Stellar.Infrastructure/Game/Protobuf/SyncNearEntitiesReader.cs` |
| Hand the result to Application | Application interface, Infrastructure caller | `src/Stellar.Application/Abstractions/ICombatEventSink.cs`, `src/Stellar.Infrastructure/Game/AppearBuffSeed.cs` |
| Store it and queue an event | Application | `src/Stellar.Application/Services/CombatService.BuffSeed.cs` |
| Public type plugins see | Abstractions | `src/Stellar.Abstractions/Domain/CombatEvent.cs` |
| Tests | Tests | `tests/Stellar.Application.Tests/Combat/Protobuf/SyncNearEntitiesBuffSeedTests.cs`, `AppearBuffSeedTests.cs`, `tests/Stellar.Application.Tests/Combat/CombatServiceBuffReplaceTests.cs` |

## 1. Find the field in the reader's schema

Each reader documents the message layout it parses in its `<summary>`. For `SyncNearEntities`
([`../../src/Stellar.Infrastructure/Game/Protobuf/SyncNearEntitiesReader.cs`](../../src/Stellar.Infrastructure/Game/Protobuf/SyncNearEntitiesReader.cs)):

```csharp
///   message Entity {
///     int64 uuid                 = 1;
///     EEntityType ent_type        = 2;
///     AttrCollection attrs        = 3;
///     TempAttrCollection temp_attrs = 4;
///     ActorBodyPartInfos body_part_infos = 5;
///     SeqPassiveSkillInfo passive_skill_infos = 6;
///     BuffInfoSync buff_infos     = 7;
///     ...
```

So field 7 is a length-delimited `BuffInfoSync`. Its inner layout, `BuffInfoSync{uuid=1, buff_infos=2
repeated BuffInfo}`, is documented on the same reader, and a `BuffInfoReader` for a single `BuffInfo`
already existed for the live buff-delta path (`BuffEffectSyncReader`). Reuse existing sub-readers wherever the same message type
appears elsewhere.

Method ids live in [`../../src/Stellar.Wire/WorldNtfMethodIds.cs`](../../src/Stellar.Wire/WorldNtfMethodIds.cs)
(`SyncNearEntities = 6` on the `WorldNtf` service, uuid `1664308034`).

## 2. Capture real bytes

Don't write a decoder from the schema alone. Run the game with `STELLAR_WIRECAP` set to capture the
service (setup and output format: [dev-environment.md](dev-environment.md#wire-capture-stellar_wirecap)):

```
STELLAR_WIRECAP=svc:1664308034
```

Then look at lines with `"method":6`. The `decoded` field is a schema-less walk, so you can see whether
field 7 is present, how often, how large it gets, and what the nested fields hold. The capture for this
change showed something the schema doesn't tell you: a town crowd arriving in one tick produces a single
`SyncNearEntities` of roughly 100 KB, with many buffs per player (the capture walker's node budget was
raised for it in `3717d02`). That volume shaped the event design in step 4.

## 3. Decode it: pure, span-based, never throws

Readers are static functions over `ReadOnlySpan<byte>` built on the helpers in `Stellar.Wire`
(`WireProtocol.TryReadTag`, `TryReadVarint`, `TryReadLengthDelimited`, `SkipField`). They return `false`
or skip on bad input; they never throw, because they run on the network thread for every packet.

The change added one `case` to the entity loop and kept every other field skipped:

```csharp
case (7, 2):
    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var buffSync)) return false;
    MergeBuffInfoSync(buffSync, ref buffs, ref buffsUnknown);
    break;

default:
    if (!WireProtocol.SkipField(payload, ref pos, wire)) return false;
    break;
```

Decide what "malformed" means for your field, and write it down in the reader's docs:

- **A bad entry in a list of independent items**: drop that one entry and keep the rest. The top-level
  `TryReadAppearAndDisappear` does this: a malformed `Entity` is dropped, the other entities still
  surface.
- **A snapshot that must be complete to be correct**: all or nothing. A buff list missing one entry
  would silently remove that buff from the consumer, so `ReadBuffInfoSync` returns `null` and sets
  `unknown` on any failure, and the caller keeps the old set:

```csharp
/// <summary>Decode <c>BuffInfoSync{uuid=1, buff_infos=2 repeated BuffInfo}</c> with the shared
/// <see cref="BuffInfoReader"/>. All-or-nothing: any framing or <c>BuffInfo</c> failure sets
/// <paramref name="unknown"/> and returns null — a partial list must never pass for the full set.
```

- **Protobuf merge rules apply.** An embedded message field that occurs twice is merged. Here the entries
  of both occurrences accumulate, and one bad occurrence marks the whole set unknown (`MergeBuffInfoSync`).

Distinguish "field absent" from "field present but empty" if the difference matters. `AppearEntityMsg`
carries `Buffs = null` when field 7 is absent and `BuffsUnknown = true` when it was present but not
decodable.

## 4. Carry it to Application on the network thread

The probe that receives the packet runs on the network thread. It must not call plugins or touch Unity.
It hands decoded data to an **outbound interface that Application declares**, and Application queues
events for the main thread.

The seed went through a new member on the existing buff sink in
[`../../src/Stellar.Application/Abstractions/ICombatEventSink.cs`](../../src/Stellar.Application/Abstractions/ICombatEventSink.cs):

```csharp
internal interface ICombatBuffSink
{
    ...
    void ReplaceEntityBuffs(EntityId entityId, IReadOnlyList<ActiveBuff>? buffs, long timestampMs);
}
```

The "skip unknown snapshots" decision is a small pure helper so it can be tested without the probe
([`../../src/Stellar.Infrastructure/Game/AppearBuffSeed.cs`](../../src/Stellar.Infrastructure/Game/AppearBuffSeed.cs)):

```csharp
public static bool Apply(ICombatBuffSink sink, EntityId entityId, in AppearEntityMsg entity, long timestampMs)
{
    if (entity.BuffsUnknown) return false;
    sink.ReplaceEntityBuffs(entityId, entity.Buffs, timestampMs);
    return true;
}
```

and the probe calls it for each appearing entity (`PandaCombatStubProbe.Receive.cs`, `OnNearEntities`):

```csharp
if (!AppearBuffSeed.Apply(_sink, eid, entity, ts)) DiagBuffSeedSkipped(eid);   // full set (field 7)
```

On the Application side, `CombatService.ReplaceEntityBuffs` updates its store under a lock and
**enqueues** the event. It never raises it directly
([`../../src/Stellar.Application/Services/CombatService.BuffSeed.cs`](../../src/Stellar.Application/Services/CombatService.BuffSeed.cs)):

```csharp
_spec.NoteSeed(entityId, snapshot, timestampMs);
if (!announce) return;
DiagBuffSeed(entityId, snapshot.Count);
EnqueueEvent(new CombatEvent.EntityBuffsSeeded(timestampMs, entityId, snapshot));
```

`CombatService.Drain` runs once per game tick on the main thread and fans queued events out to
`ICombatEvents.CombatEventOccurred`. That is the thread plugins are called on.

Design the event for the volume you measured. A per-buff `Applied` event for a 30-player crowd with
~120 buffs each would be thousands of events in one tick; one `EntityBuffsSeeded` per entity is 30.

## 5. Expose it in `Stellar.Abstractions`

The public type is the contract, so its XML docs are where you state the semantics a plugin author needs:
when it fires, what it replaces, what happens on malformed input, which thread. From
[`../../src/Stellar.Abstractions/Domain/CombatEvent.cs`](../../src/Stellar.Abstractions/Domain/CombatEvent.cs):

```csharp
/// <param name="TimestampMs">Wire receive time of the snapshot packet (client wall clock, Unix ms) — the same
/// clock its sibling <see cref="BuffChanged"/> events carry.</param>
/// <param name="TargetId">The entity whose buff set was seeded.</param>
/// <param name="Buffs">The complete buff set; empty when the entity carries none. Read-only (the same
/// instance <see cref="Services.ICombatLookup.BuffsFor"/> returns until the set next changes).</param>
public sealed record EntityBuffsSeeded(long TimestampMs, EntityId TargetId, IReadOnlyList<ActiveBuff> Buffs) : CombatEvent(TimestampMs);
```

The change was additive: a new nested record on an existing event type. Existing members keep their
meaning, and the `BuffChangeKind` docs were extended to say how later deltas for a seeded buff are
reported. Rules for changing the API, and the version bump, are in [api-changes.md](api-changes.md).

A plugin consumes it like any other combat event, on the main thread:

```csharp
services.CombatEvents.CombatEventOccurred += e =>
{
    if (e is CombatEvent.EntityBuffsSeeded seed) { /* seed.TargetId, seed.Buffs */ }
};
```

## 6. Test it offline

Every step above is testable without the game. Tests build protobuf bytes with the `WireBytes` helper
and assert on the reader output
([`../../tests/Stellar.Application.Tests/Combat/Protobuf/SyncNearEntitiesBuffSeedTests.cs`](../../tests/Stellar.Application.Tests/Combat/Protobuf/SyncNearEntitiesBuffSeedTests.cs)):

```csharp
[Fact]
public void Appear_MalformedBuffInfo_MarksBuffsUnknown()
{
    var bad = new byte[] { 0x08, 0x80 };   // truncated varint inside one BuffInfo
    var sync = BuffInfoSync(TalentBuff(11, 2202110, 510), bad, TalentBuff(13, 2202112, 512));
    var payload = new WireBytes().Tag(1, 2).LengthDelimited(Entity(sync)).ToArray();

    Assert.True(SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out _));
    var e = Assert.Single(appears);
    Assert.True(e.BuffsUnknown);
    Assert.Equal((long)PlayerUuid, e.Uuid);   // the entity itself still surfaces
}
```

The 2.11.0 change pinned: field 7 decoded, field 7 absent, a malformed entry, a truncated frame, 150
buffs in one snapshot, field 7 occurring twice (good and bad), and the same decode through `EnterScene`.
`AppearBuffSeedTests` pins the skip-on-unknown helper with a recording sink, and
`CombatServiceBuffReplaceTests` pins the service: one seed event per entity, no per-buff flood. Build
fixtures from what your capture showed, not only from the schema. More in [testing.md](testing.md).

## 7. Write the changelog entry

Add bullets under the release's `### Added` in [`../../CHANGELOG.md`](../../CHANGELOG.md). Those
bullets are shown to players in the launcher, so write what a player notices, with no identifiers. The
2.11.0 entry:

```markdown
### Added
- Mods now know the buffs a player already has the moment they come near you, instead of only after those buffs change.
```

Technical detail goes under `### Developer notes` in the same release, which the release pipeline
leaves out of the launcher. The 2.11.0 note named the new record and the behaviour change:

> `CombatEvent.EntityBuffsSeeded(TimestampMs, TargetId, Buffs)` — `SyncNearEntities` / EnterScene now
> decode `Entity.buff_infos` and replace the entity's buff set silently, one seed event per entity

## Try it yourself: `temp_attrs` (field 4)

`Entity.temp_attrs` (field 4, a `TempAttrCollection`) is not decoded anywhere yet. `TryReadEntity`
skips it, and a plugin author has asked for it. It is a good first contribution because the whole path
above already exists.

What you need to find out first. The repo documents only the field's type name; the layout of
`TempAttrCollection` is not written down anywhere in it, so don't guess it:

1. **Capture it.** Run with `STELLAR_WIRECAP=svc:1664308034` and look at field 4 inside the field-1
   entities of `"method":6` lines. Note which entities carry it, how often, and what the nested
   field numbers and values look like.
2. **Get the real schema.** Match what you see against the game's own protobuf definitions. The
   message types are generated C# in the game's code, which you can inspect locally with Cpp2IL
   (`tools/setup-dev-env.sh` downloads it); community dumps of the game's data and Lua are another
   source, and several readers cite them in their comments. Don't commit decompiler output.
3. **Check the delta path.** `AoiSyncDeltaReader` lists `TempAttrs` among the `AoiSyncDelta` fields it
   skips, without a field number. If temp attributes change after an entity appears, you'll need that
   field too, or the value will go stale.
4. **Decide the public shape.** What does a plugin want: a lookup (like `ICombatLookup`), an event, or
   both? Is a partial decode acceptable, or is it all-or-nothing like the buff set?

Files you would touch:

| File | Change |
|---|---|
| `src/Stellar.Infrastructure/Game/Protobuf/SyncNearEntitiesReader.cs` | A `case (4, 2)` in `TryReadEntity`, a new field on `AppearEntityMsg`, schema docs |
| A new `…TempAttr…Reader.cs` next to it | The `TempAttrCollection` decoder, if it isn't trivial |
| `src/Stellar.Infrastructure/Game/Protobuf/AoiSyncDeltaReader.cs` | The delta field, if step 3 says so |
| `src/Stellar.Application/Abstractions/ICombatEventSink.cs` | A sink member (on one of the narrow interfaces, keeping each at 8 members or fewer) |
| `src/Stellar.Infrastructure/Game/PandaCombatStubProbe.Receive.cs` | Pass the decoded value to the sink in `OnNearEntities` |
| `src/Stellar.Application/Services/` | Store it and queue an event, in a new `CombatService.<Feature>.cs` partial |
| `src/Stellar.Abstractions/` | The public type or member, with XML docs |
| `tests/Stellar.Application.Tests/Combat/Protobuf/` | Reader tests from bytes shaped like your capture |
| `CHANGELOG.md`, `src/Stellar.Abstractions/Domain/FrameworkVersion.cs` | A minor-version entry ([api-changes.md](api-changes.md)) |

Open the pull request with a note of what you captured and how you confirmed the schema.
