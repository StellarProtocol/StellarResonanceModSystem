# Architecture diagrams

Seven diagrams describe the framework and how to work on it. Each one is authored as a small JSON spec in
[`src/`](src) and rendered with [Archify](https://github.com/tt-a1i/archify). The committed `.svg`
files are the static exports the Markdown docs embed. Running the build also produces an
**interactive** HTML version of each diagram, with pan/zoom, search, guided views, relationship
tracing, and (on the layer diagram) clickable links to the source files.

| Diagram | Shows | Used in |
|---|---|---|
| [Framework layers](framework-layers.svg) | The assemblies, the dependency rule, and the game boundary | [README](../../README.md#architecture), [architecture.md](../architecture.md#component-layout), [coding-standards.md](../contributing/coding-standards.md) |
| [Wire packet → plugin event](wire-to-plugin.svg) | How a server packet is hooked, parsed and queued on the network thread, then fanned out to plugins on the main thread | [architecture.md](../architecture.md#component-layout), [plugin-development.md](../plugin-development.md#threading) |
| [Plugin lifecycle](plugin-lifecycle.svg) | Discovery, construction, running, disable/enable, constructor failure and retry, shutdown | [plugin-development.md](../plugin-development.md#lifecycle-and-the-dispose-contract) |
| [Ecosystem](ecosystem.svg) | How a pull request becomes a release that players get through the launcher | [CONTRIBUTING.md](../../CONTRIBUTING.md), [api-changes.md](../contributing/api-changes.md#versioning) |
| [Install paths](install-paths.svg) | Launcher vs. manual install, and what lands in the game folder | [getting-started.md](../getting-started.md) |
| [Build paths](build-paths.svg) | Stub build (what CI runs) vs. building against your own client and deploying | [dev-environment.md](../contributing/dev-environment.md) |
| [Test gates](test-gates.svg) | What local tests, CI and the release gate each prove | [testing.md](../contributing/testing.md#what-ci-runs) |

## Editing a diagram

1. Edit the JSON in `src/`. Keep it truthful to the code: nodes and labels use real type and file
   names. `framework-layers.architecture.json` pins `meta.repository.revision` to a commit, and each
   component's `sources` must exist at that commit. After moving or renaming a referenced file, bump
   the revision.
2. Rebuild and re-export:

   ```bash
   docs/diagrams/build.sh --svg        # needs Node 22+, Archify and Google Chrome
   ```

   `build.sh` runs Archify's `deliver` step for each spec at the `showcase` quality bar, and fails
   if any label overlaps, a route crosses a node, or text becomes too small to read. With `--svg`
   it then exports the static SVGs through the viewer's own exporter. The interactive HTML lands in
   `docs/diagrams/out/`, which is gitignored; open it in a browser to explore.
3. Commit the JSON and the regenerated SVGs together.

## Authoring tips (measured while building these seven)

- **Width drives readability.** The `showcase` check projects the diagram into the ~930 px panel a 1440×900
  laptop gives it and fails if any text drops below ~6 px. An architecture layout 1,790 units wide failed (scale
  0.52); ~1,320–1,360 wide passes. Keep nodes ~150–170 wide and put a second row underneath rather than
  stretching one row across.
- **Labelled arrows need room.** The gap between two nodes must be wider than the label on the arrow joining them
  — about 110 units for labels like `implements ports`. For vertical arrows, set `fromSide`/`toSide` and a
  `labelAt` beside the line; the auto-placed label otherwise sits on a node.
- **Sequence diagrams:** with seven participants at a 1,080-unit viewBox a participant box is ~118 units, too
  narrow for long type names — use a short label and put the class name in `sublabel`. Message `y` values must
  stay between 160 and the viewBox height minus ~83; set `meta.viewBox` height accordingly.
- **Source links** (`sources` on architecture components) must be *files* that exist at the pinned
  `meta.repository.revision`; `build.sh` verifies them with `--repo-root`, which only architecture diagrams accept.
- After `build.sh`, open `out/<name>.html` and look: `visual-check` (Archify's browser pass) catches overflow, but a
  label that sits away from its arrow still passes every check.

Set `ARCHIFY=/path/to/archify/bin/archify.mjs` if Archify is not installed at the default skill
path, and `CHROME=/path/to/chrome` if Chrome is not on `PATH` as `google-chrome`.
