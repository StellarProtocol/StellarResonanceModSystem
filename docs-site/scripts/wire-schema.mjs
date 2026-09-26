// Extracts the protobuf message schemas that the framework's wire readers document in their XML doc
// comments (`/// message Entity { int64 uuid = 1; … }`), so the site's Wire coverage page is built from
// the same text a contributor reads in the reader. Pure: (repo root) → [{ message, source, line, fields }].
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const ROOTS = ['src/Stellar.Infrastructure', 'src/Stellar.Wire'];
/** Illustrative snippets in docs that are not real game messages. */
const NOT_MESSAGES = new Set(['SomeGrpcTeamNtfMethod']);

const walk = (dir) => readdirSync(dir).flatMap((f) => {
  const p = join(dir, f);
  return statSync(p).isDirectory() ? walk(p) : p.endsWith('.cs') ? [p] : [];
});

const unescape = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');

/** A field declaration inside a message body (comment already stripped). */
const FIELD = /(?:^|[;{\s])(repeated\s+|optional\s+)?(map\s*<[^>]+>|[A-Za-z_][\w.]*)\s+([A-Za-z_]\w*)\s*=\s*(\d+)\s*;?/g;

export function readWireSchema(repo) {
  const byName = new Map();
  for (const file of ROOTS.flatMap((r) => walk(join(repo, r)))) {
    const src = readFileSync(file, 'utf8').split('\n');
    const rel = relative(repo, file).split('\\').join('/');
    // Only XML-doc comment lines carry schema; keep their line numbers.
    const lines = src.map((l, i) => ({ i, m: l.match(/^\s*\/\/\/(.*)$/) })).filter((x) => x.m)
      .map(({ i, m }) => ({ line: i + 1, text: unescape(m[1]) }));
    for (let k = 0; k < lines.length; k++) {
      const head = lines[k].text.match(/\bmessage\s+([A-Za-z_]\w*)\s*\{/);
      if (!head || NOT_MESSAGES.has(head[1])) continue;
      // Collect the body up to the matching brace (may be on the same line or many lines later).
      let depth = 0, body = '', done = false;
      for (let j = k; j < lines.length && !done; j++) {
        let t = lines[j].text.replace(/\/\/.*$/, ''); // drop trailing // comments
        if (j === k) t = t.slice(t.indexOf('{'));
        for (const ch of t) {
          if (ch === '{') { depth++; if (depth === 1) continue; }
          if (ch === '}') { depth--; if (depth === 0) { done = true; break; } }
          if (depth >= 1) body += ch;
        }
        body += '\n';
      }
      const fields = [];
      for (const f of body.matchAll(FIELD)) {
        fields.push({ num: Number(f[4]), name: f[3], type: (f[1] ? f[1].trim() + ' ' : '') + f[2].replace(/\s+/g, ' ') });
      }
      if (!fields.length) continue;
      const name = head[1];
      const prev = byName.get(name);
      if (!prev || fields.length > prev.fields.length) byName.set(name, { message: name, source: rel, line: lines[k].line, fields });
    }
  }
  return [...byName.values()].sort((a, b) => a.message.localeCompare(b.message));
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const schema = readWireSchema(process.argv[2] ?? join(import.meta.dirname, '../..'));
  for (const m of schema) console.log(`${m.message} (${m.source}:${m.line}): ${m.fields.map((f) => `${f.num}:${f.name}`).join(' ')}`);
  console.log(`${schema.length} messages, ${schema.reduce((n, m) => n + m.fields.length, 0)} fields`);
}
