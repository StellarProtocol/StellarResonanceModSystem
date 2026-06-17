# Contributing

The StellarResonance mod framework. Clean-architecture layers under `src/` (see
[`docs/architecture.md`](docs/architecture.md)); coding standards are mechanically enforced (file/method
size, layer rules, analyzer `STELLAR0001-0005`) — read `docs/coding-standards.md` before non-trivial work.

## How CI builds this repo (no game install needed)

`Infrastructure`/`Host` reference the game's IL2CPP interop, which can't live on public CI. So CI builds
the **whole framework against committed interop reference stubs** in [`refs/`](refs) — API-only
assemblies (no method bodies, no game IL/metadata, no `Panda.*` game logic, which the framework binds by
reflection at runtime). `ci.yml` runs the coding-standards gate, then
`dotnet build src/Stellar.Host/... -p:GameInterop=$PWD/refs -p:BepInExCore=$PWD/refs` (building `Host`
pulls in Infrastructure/Application/Wire/Abstractions; the analyzer runs at error severity across them).

**Regenerate the stubs after any game/engine patch** (do it as part of the DevKit's `/recon`) and commit:

```bash
tools/gen-refs.sh /path/to/<game_mini>      # needs: dotnet tool install -g JetBrains.Refasmer.CliTool
```

A stub build validates the **API surface, not runtime** — an *added* member fails the build loudly, but
a *changed/removed* signature can compile green yet break in-game. That's why releases are gated (below).

## Releasing

1. Bump `src/Stellar.Abstractions/Domain/FrameworkVersion.cs` and add the `CHANGELOG.md` section.
2. Merge to `main` (CI builds against stubs).
3. **Tag `vX.Y.Z`** (or run `release.yml` manually). The `build` job assembles the drop-in bundle
   (incl. `Stellar.PluginContracts.dll`, which cooperating plugins load at runtime) against stubs and
   writes `version.json`; the `publish` job uploads to the `stellar` MinIO bucket and creates the GitHub
   release — **after the `Production` environment approval**, which is the mandatory real-interop /
   in-game smoke. Don't approve on a green stub build alone.

Required repo config: secrets `S3_ACCESS_KEY`/`S3_SECRET_KEY` and `RELEASE_ASSETS_SSH_KEY` (read-only
deploy key for the private `stellar-release-assets` repo that holds the BepInEx stage), plus a
`Production` environment with required reviewers. The DevKit's `tools/release-framework.sh` is a local
fallback that builds against a real interop install.

## License

By contributing you agree your contribution is licensed under **AGPL-3.0**.
