// Generates every non-hand-written page of docs.stellarresonance.app from the framework repo:
//   1. guides/architecture/contribute pages  ← ../docs/*.md, ../CONTRIBUTING.md   (sync + link rewrite)
//   2. API reference                         ← Stellar.Abstractions XML docs     (dotnet build + xmldocmd)
//   3. changelog + "what's new" partial      ← ../CHANGELOG.md
//   4. diagrams                              ← ../docs/diagrams (static SVGs; interactive HTML when built)
// Output lands in gitignored paths (see ../.gitignore) — edit the SOURCE files, never the output.
//   node scripts/gen-all.mjs [--skip-api]
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync, copyFileSync } from 'node:fs';
import { dirname, join, posix, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readWireSchema } from './wire-schema.mjs';

const SITE = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const REPO = resolve(SITE, '..');
const CONTENT = join(SITE, 'src/content/docs');
const GENERATED = join(SITE, 'src/generated');
const PUBLIC_DIAGRAMS = join(SITE, 'public/diagrams');
const GITHUB = 'https://github.com/StellarProtocol/StellarResonanceModSystem/blob/main';
const skipApi = process.argv.includes('--skip-api');
const HARMONYX_VERSION = '2.10.2'; // must match refs/0Harmony.dll (AssemblyVersion 2.10.2.0)

/** Repo-relative source doc → site slug. Only these docs are published; everything else links to GitHub. */
const PAGES = {
  'docs/getting-started.md': 'guides/getting-started',
  'docs/plugin-development.md': 'guides/plugin-development',
  'docs/architecture.md': 'architecture',
  'CONTRIBUTING.md': 'contribute',
};
// Every contributor guide under docs/contributing/ is published as /contribute/<name>/.
if (existsSync(join(REPO, 'docs/contributing'))) {
  for (const f of readdirSync(join(REPO, 'docs/contributing')).filter((f) => f.endsWith('.md')).sort()) {
    PAGES[`docs/contributing/${f}`] = `contribute/${f.replace(/\.md$/, '')}`;
  }
}
const SLUG_FILE = (slug) => join(CONTENT, `${slug}.md`);

const log = (...a) => console.log('[gen]', ...a);
/** Headline numbers for the home hero chips (written last, so a skipped step keeps its previous value). */
const STATS = {};
const write = (file, text) => { mkdirSync(dirname(file), { recursive: true }); writeFileSync(file, text); };
const yaml = (s) => JSON.stringify(s); // JSON strings are valid YAML scalars

// ---------------------------------------------------------------- shared markdown helpers
/** Split off the first `# Title` line; returns [title, body]. */
function takeTitle(md, fallback) {
  const m = md.match(/^#\s+(.+?)\s*$/m);
  if (!m) return [fallback, md];
  return [m[1].replace(/`/g, ''), md.slice(0, m.index) + md.slice(m.index + m[0].length)];
}

function frontmatter(fields) {
  return '---\n' + Object.entries(fields).filter(([, v]) => v !== undefined)
    .map(([k, v]) => `${k}: ${typeof v === 'string' ? yaml(v) : v}`).join('\n') + '\n---\n';
}

const HTML_TAGS = new Set(['a', 'b', 'i', 'em', 'strong', 'code', 'pre', 'br', 'hr', 'p', 'div', 'span', 'img', 'sub', 'sup',
  'details', 'summary', 'kbd', 'table', 'thead', 'tbody', 'tr', 'td', 'th', 'ul', 'ol', 'li', 'small', 'mark', 'picture', 'source']);

/** Prose like "<Spec> Spec" or "<game_mini>/stellar" is a placeholder, not HTML — escape it (outside `code`),
 *  or Markdown would swallow it as an unknown tag. */
function escapePlaceholders(line) {
  return line.split('`').map((part, i) => (i % 2 ? part : part.replace(/<\/?([A-Za-z][\w-]*)([^<>]*)>/g,
    (tag, name) => (HTML_TAGS.has(name.toLowerCase()) ? tag : tag.replace(/</g, '&lt;').replace(/>/g, '&gt;'))))).join('`');
}

/** Rewrite Markdown link/image targets with `map(targetRepoPath, anchor) → url | null`. Skips fenced code. */
function rewriteLinks(md, fromRepoPath, map) {
  const fromDir = posix.dirname(fromRepoPath);
  const out = [];
  let inFence = false;
  for (const line of md.split('\n')) {
    if (/^\s*(```|~~~)/.test(line)) inFence = !inFence;
    if (inFence) { out.push(line); continue; }
    out.push(escapePlaceholders(line).replace(/(!?\[[^\]]*\])\(([^)\s]+)(\s+"[^"]*")?\)/g, (all, text, target, title = '') => {
      if (/^(https?:|mailto:|#)/.test(target)) return all;
      const [path, anchor = ''] = target.split('#');
      const repoPath = posix.normalize(posix.join(fromDir, path));
      const url = map(repoPath, anchor ? `#${anchor}` : '');
      return `${text}(${url ?? `${GITHUB}/${repoPath}${anchor ? `#${anchor}` : ''}`}${title})`;
    }));
  }
  return out.join('\n');
}

const apiSlug = (repoPath) => // docs/api/Stellar.Abstractions.Services/ICombatSpec.md → api/stellar-abstractions-services/icombatspec
  'api/' + repoPath.replace(/^docs\/api\//, '').replace(/\.md$/, '').split('/')
    .map((seg) => seg.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, ''))
    // A member literally named Index (LoadoutSlot/Index.md) would collide with its folder's index page.
    .map((seg, i, all) => (seg === 'index' && i === all.length - 1 && all.length > 1 ? 'index-member' : seg)).join('/');

/** Site-wide link map for synced docs. */
function siteLink(repoPath, anchor) {
  if (PAGES[repoPath]) return `/${PAGES[repoPath]}/${anchor}`;
  if (repoPath === 'docs/diagrams/README.md' || repoPath === 'docs/diagrams') return `/architecture/diagrams/${anchor}`;
  if (repoPath.startsWith('docs/diagrams/') && repoPath.endsWith('.svg')) return `/diagrams/${posix.basename(repoPath)}`;
  if (repoPath === 'docs/api' || repoPath === 'docs/api/README.md' || repoPath === 'docs/api/Stellar.Abstractions.md') return `/api/${anchor}`;
  if (repoPath.startsWith('docs/api/') && repoPath.endsWith('.md')) return `/${apiSlug(repoPath)}/${anchor}`;
  return null;
}

// ---------------------------------------------------------------- 1. synced docs
/** A static diagram image in a synced doc (GitHub shows the SVG) becomes, on the site, the same image plus an
 *  "Explore interactive" button that opens the live Archify diagram full-screen (public/js/diagram-lightbox.js). */
function interactiveFigures(md) {
  return md.replace(/^!\[([^\]]*)\]\(\/diagrams\/([\w-]+)\.svg\)\s*$/gm, (all, alt, name) => {
    if (!existsSync(join(PUBLIC_DIAGRAMS, `${name}.html`))) return all;
    const a = alt.replace(/"/g, '&quot;');
    return `<figure class="st-fig">\n<img src="/diagrams/${name}.svg" alt="${a}" />\n` +
      `<figcaption><button type="button" class="st-explore" data-diagram="/diagrams/${name}.html" data-title="${a}">▶ Explore interactive</button>` +
      `<span>pan · zoom · search · click a box for its source</span></figcaption>\n</figure>`;
  });
}
function syncDocs() {
  for (const [src, slug] of Object.entries(PAGES)) {
    const [title, body] = takeTitle(readFileSync(join(REPO, src), 'utf8'), slug);
    const md = interactiveFigures(rewriteLinks(body, src, siteLink));
    write(SLUG_FILE(slug), frontmatter({ title, sourcePath: src }) + md);
  }
  log(`synced ${Object.keys(PAGES).length} docs`);
}

// ---------------------------------------------------------------- 2. API reference
function walk(dir) {
  return readdirSync(dir).flatMap((f) => {
    const p = join(dir, f);
    return statSync(p).isDirectory() ? walk(p) : [p];
  });
}

async function genApi() {
  const dotnet = process.env.DOTNET || (existsSync(join(process.env.HOME ?? '', '.dotnet/dotnet')) ? join(process.env.HOME, '.dotnet/dotnet') : 'dotnet');
  const proj = join(REPO, 'src/Stellar.Abstractions/Stellar.Abstractions.csproj');
  log('building Stellar.Abstractions (XML docs)…');
  execFileSync(dotnet, ['build', proj, '-c', 'Release', '--nologo', '-v', 'quiet', '-o', join(SITE, '.api-build')], { stdio: 'inherit' });
  // Abstractions' one external reference (IHarmonyHost.Create returns HarmonyLib.Harmony) is Private=false and
  // bound to the refs/ stub — a REFERENCE assembly, which reflection refuses to load. xmldocmd needs a real
  // 0Harmony beside the DLL, so fetch the matching HarmonyX (MIT) from NuGet. Docs generation only; never shipped.
  const harmony = join(SITE, '.api-build/0Harmony.dll');
  if (!existsSync(harmony)) {
    const nupkg = join(SITE, '.api-build/harmonyx.nupkg');
    log(`fetching HarmonyX ${HARMONYX_VERSION} (0Harmony for reflection)…`);
    const res = await fetch(`https://www.nuget.org/api/v2/package/HarmonyX/${HARMONYX_VERSION}`);
    if (!res.ok) throw new Error(`HarmonyX download failed: HTTP ${res.status}`);
    writeFileSync(nupkg, Buffer.from(await res.arrayBuffer()));
    execFileSync('unzip', ['-o', '-q', '-j', nupkg, 'lib/netstandard2.0/0Harmony.dll', '-d', join(SITE, '.api-build')]);
  }
  const raw = join(SITE, '.api-raw');
  rmSync(raw, { recursive: true, force: true });
  // xmldocmd 2.9 targets net7; roll forward onto whatever runtime the SDK brought (8/10).
  const localTool = join(process.env.HOME ?? '', '.dotnet/tools/xmldocmd');
  const xmldocmd = process.env.XMLDOCMD || (existsSync(localTool) ? localTool : 'xmldocmd');
  const env = { ...process.env, DOTNET_ROLL_FORWARD: 'Major' };
  if (!env.DOTNET_ROOT && dotnet.includes('/')) env.DOTNET_ROOT = dirname(dotnet);
  execFileSync(xmldocmd, [join(SITE, '.api-build/Stellar.Abstractions.dll'), raw,
    '--visibility', 'public', '--skip-compiler-generated', '--clean', '--quiet'], { stdio: 'inherit', env });

  // Type name → repo path of the .cs file that declares it (nested types resolve to their parent's file).
  const sources = new Map();
  for (const f of walk(join(REPO, 'src/Stellar.Abstractions')).filter((f) => f.endsWith('.cs'))) {
    const base = posix.basename(f, '.cs');
    if (!sources.has(base)) sources.set(base, relative(REPO, f).split('\\').join('/'));
  }
  const sourceFor = (rel) => { // Stellar.Abstractions.Domain/CombatEvent.EntityBuffsSeeded/Buffs.md
    const parts = rel.replace(/\.md$/, '').split('/');
    const type = (parts[1] ?? '').split('.')[0].replace(/-\d+$/, '');
    return sources.get(type) ?? 'src/Stellar.Abstractions';
  };

  const outRoot = join(CONTENT, 'api');
  rmSync(outRoot, { recursive: true, force: true });
  let n = 0;
  for (const file of walk(raw).filter((f) => f.endsWith('.md'))) {
    const rel = relative(raw, file).split('\\').join('/');
    const repoPath = `docs/api/${rel}`;
    const isRoot = rel === 'Stellar.Abstractions.md';
    let [title, body] = takeTitle(readFileSync(file, 'utf8'), rel);
    body = body.replace(/<!-- DO NOT EDIT[^>]*-->\s*/g, '');
    body = rewriteLinks(body, repoPath, siteLink);
    const slug = isRoot ? 'api' : apiSlug(repoPath);
    write(join(CONTENT, `${slug}${isRoot ? '/index' : ''}.md`),
      frontmatter({ title: isRoot ? 'API reference' : title, sourcePath: isRoot ? 'src/Stellar.Abstractions' : sourceFor(rel), generated: true }) + body);
    n++;
  }
  STATS.apiPages = n;
  log(`generated ${n} API pages`);
}

// ---------------------------------------------------------------- 3. changelog + what's new
function genChangelog() {
  const text = readFileSync(join(REPO, 'CHANGELOG.md'), 'utf8');
  const start = text.search(/^## \[/m);
  const releases = text.slice(start);
  write(join(CONTENT, 'changelog.md'),
    frontmatter({ title: 'Changelog', description: 'Every framework release — player notes and developer notes.', sourcePath: 'CHANGELOG.md', generated: true })
    + rewriteLinks(releases, 'CHANGELOG.md', siteLink));

  // Latest release's Developer notes → a partial the home page renders.
  const m = releases.match(/^## \[([^\]]+)\] - (\d{4}-\d{2}-\d{2})\n([\s\S]*?)(?=^## \[)/m);
  const [, version, date, section] = m;
  const summary = (section.match(/^_(.+)_\s*$/m)?.[1] ?? '').replace(/\*\*/g, '');
  const dev = section.match(/^### Developer notes\n([\s\S]*?)(?=^### |\s*$(?![\s\S]))/m)?.[1]?.trim() ?? '';
  write(join(GENERATED, 'whats-new.md'), rewriteLinks(dev, 'CHANGELOG.md', siteLink) + '\n');
  write(join(GENERATED, 'release.json'), JSON.stringify({ version, date, summary }, null, 2) + '\n');
  log(`changelog + what's new (${version})`);
}

// ---------------------------------------------------------------- 7. versions
/** Production (docs.stellarresonance.app) is built from main. Every release tag vX.Y.Z also deploys a frozen
 *  snapshot as the Pages branch alias vX-Y-Z — the version menu lists the tags newer than the site's launch. */
const SNAPSHOTS_AFTER = '2.11.0'; // the site launched after 2.11.0; older tags have no docs-site/ to build
const PAGES_HOST = 'stellar-docs-bs5.pages.dev';
function genVersions() {
  const current = readFileSync(join(REPO, 'src/Stellar.Abstractions/Domain/FrameworkVersion.cs'), 'utf8')
    .match(/const string Value\s*=\s*"([^"]+)"/)[1];
  const cmp = (a, b) => { const x = a.split('.').map(Number), y = b.split('.').map(Number); for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i]; return 0; };
  let tags = [];
  try { tags = execFileSync('git', ['tag', '--list', 'v*'], { cwd: REPO }).toString().split('\n'); } catch { /* no git */ }
  const snapshots = tags.map((t) => t.trim().match(/^v(\d+\.\d+\.\d+)$/)?.[1]).filter(Boolean)
    .filter((v) => cmp(v, SNAPSHOTS_AFTER) > 0).sort((a, b) => cmp(b, a))
    .map((v) => ({ version: v, url: `https://v${v.replace(/\./g, '-')}.${PAGES_HOST}/` }));
  write(join(GENERATED, 'versions.json'), JSON.stringify({ current, snapshots }, null, 2) + '\n');
  log(`versions: current ${current}, ${snapshots.length} snapshots`);
}

// ---------------------------------------------------------------- 6. plugin gallery
/** Every plugin in the public registry (StellarResonancePlugins — manifests only, each pinning a source repo +
 *  commit). Plugins that must never be published are absent from the registry by design, so none can leak here.
 *  "APIs used" = IPluginServices members referenced in the plugin's source at its pinned commit. */
async function genPlugins() {
  const cache = join(SITE, '.plugins-cache');
  mkdirSync(cache, { recursive: true });
  const git = (args, cwd) => execFileSync('git', args, { cwd, stdio: ['ignore', 'pipe', 'pipe'] }).toString();
  const fetchAt = (dir, url, ref) => { // shallow fetch of one commit, cached by directory
    if (existsSync(join(dir, '.git'))) { try { git(['cat-file', '-e', `${ref}^{commit}`], dir); git(['checkout', '-q', ref], dir); return; } catch { /* refetch */ } }
    mkdirSync(dir, { recursive: true });
    if (!existsSync(join(dir, '.git'))) git(['init', '-q'], dir);
    git(['fetch', '-q', '--depth', '1', url, ref], dir);
    git(['checkout', '-q', 'FETCH_HEAD'], dir);
  };

  const registry = process.env.PLUGINS_REGISTRY || join(cache, 'registry');
  if (!process.env.PLUGINS_REGISTRY) fetchAt(registry, 'https://github.com/StellarProtocol/StellarResonancePlugins.git', 'main');

  const services = [...readFileSync(join(REPO, 'src/Stellar.Abstractions/Services/IPluginServices.cs'), 'utf8')
    .matchAll(/^\s+(I\w+)\s+(\w+)\s*\{\s*get;\s*\}/gm)].map((m) => ({ iface: m[1], prop: m[2] }));
  const byProp = new Map(services.map((s) => [s.prop, s.iface]));

  const media = join(SITE, 'public/plugins');
  rmSync(media, { recursive: true, force: true });
  const plugins = [];
  for (const id of readdirSync(join(registry, 'plugins')).sort()) {
    const dir = join(registry, 'plugins', id);
    if (!existsSync(join(dir, 'manifest.json'))) continue;
    const m = JSON.parse(readFileSync(join(dir, 'manifest.json'), 'utf8'));
    let apis = [];
    try {
      const src = join(cache, 'src', id);
      fetchAt(src, m.repository, m.commit);
      const code = walk(join(src, m.projectPath ?? '.')).filter((f) => f.endsWith('.cs')).map((f) => readFileSync(f, 'utf8')).join('\n');
      // `<something>Services.Prop` / `_s.Prop` style member access on the services aggregate.
      const seen = new Set([...code.matchAll(/\b\w*(?:[Ss]ervices?|[Ss]vc|_s)\.(\w+)/g)].map((x) => x[1]).filter((p) => byProp.has(p)));
      apis = [...seen].sort().map((p) => ({ prop: p, iface: byProp.get(p) }));
    } catch (e) { console.warn(`[gen] plugin ${id}: source scan skipped (${e.message.split('\n')[0]})`); }
    const img = (m.media ?? []).find((x) => x.type === 'image');
    let image;
    if (img && existsSync(join(dir, img.file))) {
      image = `/plugins/${id}/${posix.basename(img.file)}`;
      mkdirSync(join(media, id), { recursive: true });
      copyFileSync(join(dir, img.file), join(SITE, 'public', image));
    }
    plugins.push({
      id, name: m.name, description: m.description, version: m.version, author: m.author, tags: m.tags ?? [],
      date: m.date, minFramework: m.minModSystemVersion, homepage: m.homepage,
      source: m.repository.replace(/\.git$/, ''), sourceAtCommit: `${m.repository.replace(/\.git$/, '')}/tree/${m.commit}`,
      image, imageCaption: img?.caption, apis,
    });
  }
  STATS.plugins = plugins.length;
  write(join(GENERATED, 'plugins.json'), JSON.stringify(plugins, null, 2) + '\n');
  log(`plugin gallery: ${plugins.length} plugins`);
}

// ---------------------------------------------------------------- 5. wire coverage
/** Joins the schemas the readers document (wire-schema.mjs) with docs/wire/coverage.json. The coverage file must
 *  classify EVERY documented field and nothing else — drift fails the build (in CI; warns locally). */
function genWire() {
  const schema = readWireSchema(REPO);
  const coverage = JSON.parse(readFileSync(join(REPO, 'docs/wire/coverage.json'), 'utf8')).messages;
  const problems = [];
  const messages = schema.map((m) => {
    const cov = coverage[m.message]?.fields ?? {};
    if (!coverage[m.message]) problems.push(`${m.message}: documented in ${m.source} but missing from docs/wire/coverage.json`);
    for (const num of Object.keys(cov)) if (!m.fields.some((f) => String(f.num) === num)) problems.push(`${m.message}.${num}: in coverage.json but not in the schema at ${m.source}`);
    return {
      message: m.message, source: m.source, line: m.line,
      url: `${GITHUB}/${m.source}#L${m.line}`,
      fields: m.fields.map((f) => {
        const c = cov[String(f.num)];
        if (!c) problems.push(`${m.message}.${f.num} (${f.name}): not classified in docs/wire/coverage.json`);
        else if (!c.status) problems.push(`${m.message}.${f.num} (${f.name}): status is empty`);
        return { ...f, status: c?.status ?? 'unclassified', exposedAs: c?.exposedAs, note: c?.note, since: c?.since, goodFirstIssue: c?.goodFirstIssue ?? false };
      }),
    };
  });
  for (const m of Object.keys(coverage)) if (!schema.some((s) => s.message === m)) problems.push(`${m}: in coverage.json but no reader documents it`);
  if (problems.length) {
    const msg = `wire coverage out of date (${problems.length}):\n  ${problems.join('\n  ')}`;
    if (process.env.CI) throw new Error(msg);
    console.warn(`[gen] WARNING ${msg}`);
  }
  const all = messages.flatMap((m) => m.fields);
  const count = (s) => all.filter((f) => f.status === s).length;
  write(join(GENERATED, 'wire.json'), JSON.stringify({
    totals: { fields: all.length, messages: messages.length, exposed: count('exposed'), internal: count('internal'),
      skipped: count('skipped'), unclassified: count('unclassified'), goodFirstIssue: all.filter((f) => f.goodFirstIssue).length },
    messages,
  }, null, 2) + '\n');
  log(`wire coverage: ${messages.length} messages, ${all.length} fields (${count('exposed')} exposed, ${count('skipped')} skipped)`);
}

// ---------------------------------------------------------------- 4. diagrams
function copyDiagrams() {
  const src = join(REPO, 'docs/diagrams');
  mkdirSync(PUBLIC_DIAGRAMS, { recursive: true });
  let svg = 0, html = 0;
  for (const f of readdirSync(src).filter((f) => f.endsWith('.svg'))) { copyFileSync(join(src, f), join(PUBLIC_DIAGRAMS, f)); svg++; }
  const out = join(src, 'out');
  if (existsSync(out)) for (const f of readdirSync(out).filter((f) => f.endsWith('.html') && !f.includes('.visual-check'))) {
    copyFileSync(join(out, f), join(PUBLIC_DIAGRAMS, f)); html++;
  }
  write(join(GENERATED, 'diagrams.json'), JSON.stringify({ interactive: html > 0 }) + '\n');
  log(`diagrams: ${svg} svg, ${html} interactive${html ? '' : ' (run docs/diagrams/build.sh to include them)'}`);
}

copyDiagrams();
syncDocs();
genChangelog();
genWire();
genVersions();
if (!process.argv.includes('--skip-plugins')) await genPlugins(); else log('skipped plugin gallery (--skip-plugins)');
if (!skipApi) await genApi(); else log('skipped API reference (--skip-api)');

STATS.services = [...readFileSync(join(REPO, 'src/Stellar.Abstractions/Services/IPluginServices.cs'), 'utf8').matchAll(/^\s+I\w+\s+\w+\s*\{\s*get;\s*\}/gm)].length;
if (STATS.plugins === undefined && existsSync(join(GENERATED, 'plugins.json'))) STATS.plugins = JSON.parse(readFileSync(join(GENERATED, 'plugins.json'), 'utf8')).length;
STATS.wireFields = JSON.parse(readFileSync(join(GENERATED, 'wire.json'), 'utf8')).totals.fields;
const statsFile = join(GENERATED, 'stats.json');
const prev = existsSync(statsFile) ? JSON.parse(readFileSync(statsFile, 'utf8')) : {};
write(statsFile, JSON.stringify({ services: 0, apiPages: 0, plugins: 0, wireFields: 0, ...prev, ...STATS }, null, 2) + '\n');
