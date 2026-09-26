# Changing the plugin API

`Stellar.Abstractions` is the contract between the framework and every plugin. Plugins reference it and
nothing else from the framework ([`../plugin-development.md`](../plugin-development.md)). This page covers
what you may change there, how to version it, and how to document it.

## The rule: add, never break

Plugins are compiled binaries. A plugin built last month against Abstractions 2.9.0 is loaded, unchanged,
by a player running framework 2.11.0. The .NET runtime binds each call the plugin makes by its exact
type and member signature. If that member is gone or its signature changed, the plugin fails when it
runs (a missing-method or type-load error), not when anyone builds anything. CI can't catch this: it
doesn't build plugins, and the framework itself still compiles.

So changes to Abstractions are **additive only**.

| Safe (additive) | Breaking; don't |
|---|---|
| A new interface, record, enum or static type | Removing or renaming any public type or member |
| A new member on an interface the **framework** implements (a service such as `ICombatLookup`) | Adding a member to an interface **plugins** implement (such as `IStellarPlugin`); every existing plugin stops satisfying it |
| A new nested record on an existing event type (how 2.11.0 added `CombatEvent.EntityBuffsSeeded`) | Adding, removing or reordering positional parameters of an existing record; its constructor and `Deconstruct` change |
| A new overload, with the old one kept | Changing a parameter or return type, or making a member non-nullable where it was nullable |
| A new service property on `IPluginServices` | Changing what an existing member means, even with the same signature |

If an existing member is wrong, add a correct one beside it and describe the old one's limits in its XML
docs. Behaviour changes to an existing member count as breaking too: plugins were written against what
its docs promised.

## Versioning

A public API change reaches plugin authors through the release path below: the tag builds the bundle and the NuGet SDK packages, and publishing waits on the in-game smoke approval.

![Stellar ecosystem — from pull request to players](../diagrams/ecosystem.svg)

The framework uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html) (see the header of
[`../../CHANGELOG.md`](../../CHANGELOG.md)):

| Change | Version part | Examples |
|---|---|---|
| New public API in Abstractions | minor | 2.10.0 (`ILoadoutSave`), 2.11.0 (`EntityBuffsSeeded`, `SpecChanged`, `TryGetTalentSpec`) |
| Fixes and internal changes, no API change | patch | 2.8.5 (hotkey list ordering) |
| Breaking change | major | Avoid; see above |

The version lives in one place,
[`../../src/Stellar.Abstractions/Domain/FrameworkVersion.cs`](../../src/Stellar.Abstractions/Domain/FrameworkVersion.cs).
Host's `BootstrapPlugin.PluginVersion` forwards to it, so the BepInEx plugin manifest and the About panel
follow automatically. A version bump touches:

1. **`FrameworkVersion.Value`**: plain `X.Y.Z`. BepInEx parses it as SemVer and skips a plugin whose
   version has trailing letters. Add a one-line summary of the new API at the top of the history in the
   `Value` doc comment, as each release does:

   ```csharp
   /// 2.11.0 adds player spec from talent root buffs: <c>ICombatSpec.TryGetTalentSpec</c>, <c>CombatEvent.SpecChanged</c>
   /// and <c>CombatEvent.EntityBuffsSeeded</c> (a player's full buff set when they appear). Additive only.
   ```

2. **`CHANGELOG.md`**: a `## [X.Y.Z] - YYYY-MM-DD` section (format below). The release tooling reads the
   section whose heading matches the version being released
   ([`../../tools/release/changelog.py`](../../tools/release/changelog.py)), so the heading must match
   `FrameworkVersion.Value` exactly.

The release itself is cut by maintainers by tagging `vX.Y.Z`
([`../../CONTRIBUTING.md`](../../CONTRIBUTING.md#releasing)). The same workflow packs the
`Stellar.Abstractions` and `Stellar.PluginContracts` NuGet packages at that version
([`../../.github/workflows/release.yml`](../../.github/workflows/release.yml)), which is what plugin
authors build against.

## XML docs are required

`Stellar.Abstractions.csproj` turns missing-XML-doc warnings (`CS1591`) into errors. Every public type
and member, including record parameters, needs a `///` comment or the build fails. Write them for a
plugin author who can't read the framework's internals:

- **When** it fires or changes, and on **which thread** (combat events always fire on the main thread).
- What it **replaces** or accumulates, and what an empty value means.
- What happens on **incomplete data** (for example, `EntityBuffsSeeded` is skipped entirely when the
  snapshot couldn't be decoded).
- How it relates to existing members. When 2.11.0 added the seed event, the docs of `BuffChangeKind`
  were extended to say that a seeded buff's later deltas arrive as `Refreshed` or `Removed`, never
  `Applied`.

Two text-gate rules apply to Abstractions in particular: no `GUILayout`, `GUIStyle`, `GUIContent` or
`GUI.` tokens, even in comments, and no `using` of BepInEx, Harmony, Unity, Il2Cpp or game namespaces
([coding-standards.md](coding-standards.md)).

## Writing the changelog entry

The top of [`../../CHANGELOG.md`](../../CHANGELOG.md) sets the standard. The bullets under
`### Added`, `### Changed`, `### Fixed` and `### Removed` are shown **verbatim to players** in the
launcher as the patch notes (only those four headings are extracted, see
[`../../tools/release/make_version_json.py`](../../tools/release/make_version_json.py)). So:

- One short sentence per bullet, about what a player sees or can now do.
- No class names, method names or internals.
- All technical detail goes under `### Developer notes`, which stays on GitHub and the docs site but
  never reaches the launcher.
- The italic line under the version heading is a repo-only summary. It states the release type and
  whether it adds API.

A real entry, 2.11.0 (developer notes shortened):

```markdown
## [2.11.0] - 2026-09-26
_**2.11.0** (minor) — Mods can now tell which spec nearby players are playing, the moment they come near you, and know the buffs players already have when they arrive. Adds API for plugins (Abstractions 2.11.0)._
### Added
- Mods can now see which spec a nearby player is playing as soon as they come near you, even in town before any fight. Combat Meter uses this to show specs straight away.
- Mods now know the buffs a player already has the moment they come near you, instead of only after those buffs change.
### Developer notes
- New API (additive): `ICombatSpec.TryGetTalentSpec(EntityId, out int)`; `CombatEvent.SpecChanged(...)`; `CombatEvent.EntityBuffsSeeded(TimestampMs, TargetId, Buffs)` ...
```

Framework features are often invisible to players until a plugin uses them. Say what mods can now do
("Mods now know…"), and name the plugin that benefits if there is one.

## The API reference updates itself

The API pages on the docs site are generated from the XML docs on every site build: the `docs` workflow
([`../../.github/workflows/docs.yml`](../../.github/workflows/docs.yml)) builds `Stellar.Abstractions`,
runs `xmldocmd` over it, and publishes the result. It runs on any pull request or push that touches
`src/Stellar.Abstractions/`, so the reference for your new member appears with your change (pull requests from
branches of this repository also get a preview deployment). Your job is to write good XML docs; there is nothing else to update by hand.

The reference is published at <https://docs.stellarresonance.app/api/>; the repo no longer carries a committed Markdown
copy (it drifted), so there is nothing to regenerate by hand.

## Checklist

- [ ] The change is additive; no existing public member was removed, renamed or re-typed.
- [ ] Every new public member has XML docs covering when, which thread, and edge cases.
- [ ] `FrameworkVersion.Value` bumped (minor for new API) with a history line in its doc comment.
- [ ] A matching `## [X.Y.Z]` section in `CHANGELOG.md`: player bullets plus `### Developer notes`.
- [ ] Tests for the behaviour behind the new API ([testing.md](testing.md)).
- [ ] `dotnet build`, `dotnet test` and `bash tools/check-standards.sh` pass locally
      ([dev-environment.md](dev-environment.md)).
