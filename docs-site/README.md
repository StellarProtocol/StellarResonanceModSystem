# docs-site — docs.stellarresonance.app

The framework's documentation website ([Starlight](https://starlight.astro.build) on Cloudflare Pages).
Almost every page is **generated from this repo** on each build, so the site cannot drift from the code:

| Site section | Source of truth | Generator |
|---|---|---|
| Guides, Architecture, Contributing | `../docs/*.md`, `../CONTRIBUTING.md` | `scripts/gen-all.mjs` (sync + link rewrite) |
| API reference | `///` XML docs in `src/Stellar.Abstractions` | `dotnet build` + `xmldocmd` |
| Changelog, home "What's new" | `../CHANGELOG.md` (Developer notes) | `scripts/gen-all.mjs` |
| Interactive diagrams | `../docs/diagrams/src/*.json` | `../docs/diagrams/build.sh` (Archify) |

Hand-written here: only `src/content/docs/index.mdx` (home) and `src/content/docs/architecture/diagrams.mdx`.
**To change a guide, edit the file under `../docs/`** — the generated copies are gitignored.

```bash
cd docs-site
npm ci
npm run dev       # regenerate + dev server at http://localhost:4321
npm run build     # regenerate + static build into dist/
```

Needs Node 22+, the .NET 8 SDK and `dotnet tool install -g xmldocmd --version 2.9.0` (or `node scripts/gen-all.mjs
--skip-api` to skip the API reference). Interactive diagrams appear only after `../docs/diagrams/build.sh`; without
it the diagram page falls back to the static SVGs.

Deploys: `.github/workflows/docs.yml` — `main` → production, pull requests → preview URLs. Requires repo secrets
`CLOUDFLARE_API_TOKEN` (Account › Cloudflare Pages › Edit) and `CLOUDFLARE_ACCOUNT_ID`; Pages project `stellar-docs`.
