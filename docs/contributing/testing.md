# Testing

The framework's logic is tested with plain `dotnet test`. No game, BepInEx or Unity runtime is involved.
This page covers the test projects, the patterns they use, what CI runs, and what CI can't tell you.

## The test projects

| Project | Tests | Framework |
|---|---|---|
| [`../../tests/Stellar.Application.Tests/`](../../tests/Stellar.Application.Tests) | Application services, wire readers in Infrastructure, capture tooling, config and data stores | xUnit, `net8.0` |
| [`../../tests/Stellar.Analyzers.Tests/`](../../tests/Stellar.Analyzers.Tests) | The `STELLAR000x` analyzer rules themselves | xUnit + `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing`, `net8.0` |

Both are part of `src/Stellar.sln`. The test projects target `net8.0` while the framework targets
`net6.0`; they only run on a newer runtime, the framework DLLs they reference stay `net6.0`
([`../../tests/Directory.Build.props`](../../tests/Directory.Build.props)).

`Stellar.Application.Tests` references Abstractions, Wire, Application **and** Infrastructure. The
internal types it needs are opened to it with `InternalsVisibleTo` in the Application and
Infrastructure `.csproj` files. Because it references Infrastructure, building it needs the interop
paths, which the committed stubs provide.

## Running them

```bash
# everything, as CI does
dotnet build src/Stellar.sln -c Release -p:GameInterop=$PWD/refs -p:BepInExCore=$PWD/refs
dotnet test  src/Stellar.sln -c Release --no-build -p:GameInterop=$PWD/refs -p:BepInExCore=$PWD/refs

# one class while you iterate
dotnet test tests/Stellar.Application.Tests -c Release \
  -p:GameInterop=$PWD/refs -p:BepInExCore=$PWD/refs \
  --filter "FullyQualifiedName~SyncNearEntitiesBuffSeedTests"
```

The Application tests are organised by feature folder (`Combat/`, `Combat/Protobuf/`, `Wire/`,
`Party/`, `Inventory/`, …), mirroring the code they test.

## Testing a wire reader from bytes

Readers are pure functions over bytes, so their tests build a protobuf payload and assert on the
result. The fixture builder is `WireBytes`
([`../../tests/Stellar.Application.Tests/Wire/WireBytes.cs`](../../tests/Stellar.Application.Tests/Wire/WireBytes.cs)),
a small independent encoder, so a reader is never tested against bytes produced by its own code:

```csharp
/// <summary>Write a protobuf tag: (field_number &lt;&lt; 3) | wire_type, as varint.</summary>
public WireBytes Tag(int fieldNumber, int wireType)
```

Fixtures are built from small helpers that mirror the real message shape. From
[`../../tests/Stellar.Application.Tests/Combat/Protobuf/SyncNearEntitiesBuffSeedTests.cs`](../../tests/Stellar.Application.Tests/Combat/Protobuf/SyncNearEntitiesBuffSeedTests.cs):

```csharp
private static byte[] Entity(byte[]? buffSync)
{
    var w = new WireBytes().Tag(1, 0).Varint(PlayerUuid).Tag(2, 0).Varint(1);
    if (buffSync is not null) w.Tag(7, 2).LengthDelimited(buffSync);
    w.Tag(9, 0).Varint(0);
    return w.ToArray();
}

[Fact]
public void Appear_WithField7_DecodesActiveBuffsIncludingFightSource()
{
    var sync = BuffInfoSync(TalentBuff(11, 2202110, 510), TalentBuff(12, 2202111, 511));
    var payload = new WireBytes().Tag(1, 2).LengthDelimited(Entity(sync)).ToArray();

    Assert.True(SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out _));

    var e = Assert.Single(appears);
    Assert.Equal((long)PlayerUuid, e.Uuid);
    Assert.NotNull(e.Buffs);
    Assert.Equal(2, e.Buffs!.Count);
    ...
}
```

Note that the fixture includes fields the reader doesn't use (`2` and `9` here, and an unparsed field
inside each buff). That proves unknown fields are skipped rather than breaking the parse.

For a new or changed reader, cover at least:

| Case | Why |
|---|---|
| The field present and well formed | The happy path. |
| The field absent | Readers must tell "absent" from "empty" when that matters. |
| A truncated varint or a length prefix that runs past the end | Readers must return `false` or mark the data unknown, never throw. |
| The largest realistic payload | Capture a real one first; size lists from it. |
| Repeated occurrences of the field | Protobuf merge semantics. |

## Testing Application services

Services are constructed directly with test doubles for their dependencies. The doubles implement the
same interfaces as the real Infrastructure adapters: `StubLog`, `StubProbe`, `StubCombat`,
`StubClientState` and others at the root of `tests/Stellar.Application.Tests/`. For example
`CombatServiceBuffReplaceTests` builds a real `CombatService`, feeds it through its sink interface,
calls `Drain()` (what the main thread does each tick), and asserts on the events a plugin would receive:

```csharp
svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
svc.Drain();

Assert.Empty(Of<CombatEvent.BuffChanged>(events));
var seed = Assert.Single(Of<CombatEvent.EntityBuffsSeeded>(events));
```

When a probe makes a decision worth testing (such as "skip an incomplete snapshot"), extract it into a
small static helper and test that with a recording fake, as `AppearBuffSeedTests` does, instead of
trying to drive the probe itself.

## Analyzer tests

`SizeAndShapeAnalyzerTests` uses the Roslyn analyzer-testing package with diagnostic markup: the
expected diagnostic id is written inline around the identifier it should flag, for example
`void {|STELLAR0002:M|}()` for a method over 50 lines. Add a test here when you change a rule or an
exemption.

![What proves a change — local, CI, release gate](../diagrams/test-gates.svg)

## What CI runs

[`../../.github/workflows/ci.yml`](../../.github/workflows/ci.yml) runs on every pull request and every
push to `main`:

1. `bash tools/check-standards.sh`: the text gate. Fails on any blocker or major.
2. `dotnet build src/Stellar.sln -c Release -p:GameInterop=$PWD/refs -p:BepInExCore=$PWD/refs`: every
   `src` project and both test projects, against the interop stubs, with the analyzer at error severity.
3. `dotnet test src/Stellar.sln -c Release --no-build …`: both test projects.

## What CI can't tell you

The stubs in `refs/` contain the public API surface of the Unity, IL2CPP and BepInEx assemblies, with
no method bodies and no game logic. So a green CI run means the code compiles against that surface and
the managed tests pass. It does **not** mean the change works in the game:

- The framework binds many game types by reflection at runtime; a wrong name or signature compiles
  fine.
- A changed or removed interop signature can build green and still fail when the game loads it.
- Threading, timing and the real shape of server data only show up in a running client.

That is why a release is gated separately. The `publish` job in
[`../../.github/workflows/release.yml`](../../.github/workflows/release.yml) waits on a `Production`
environment approval, which maintainers give only after the build has been smoke-tested in the real
game. As a contributor:

- If you can run the game, deploy your build ([dev-environment.md](dev-environment.md#deploying-to-your-own-game)),
  exercise the change, and say in the pull request what you tested and what the log showed.
- If you can't, say so. Maintainers will test it in-game before a release; unit tests that pin the
  behaviour make that much quicker.
