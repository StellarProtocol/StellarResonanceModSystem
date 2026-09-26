// Export a static, theme-aware SVG from each rendered Archify HTML using the viewer's own
// exporter (window.Archify.exportMenu), driven through headless Chrome's DevTools protocol.
//   node export-svg.mjs <dir-with-html> <dest-dir>
import { spawn } from 'node:child_process';
import { mkdtempSync, readdirSync, renameSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const [htmlDir, destDir] = process.argv.slice(2).map(p => resolve(p));
const chromeBin = process.env.CHROME || 'google-chrome';
const port = 9333 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), 'archify-chrome-'));
const downloads = mkdtempSync(join(tmpdir(), 'archify-svg-'));
const sleep = ms => new Promise(r => setTimeout(r, ms));

const chrome = spawn(chromeBin, ['--headless=new', '--disable-gpu', `--remote-debugging-port=${port}`,
    `--user-data-dir=${profile}`, '--window-size=1600,1100', 'about:blank'], { stdio: 'ignore' });
try {
    let version;
    for (let i = 0; i < 50 && !version; i++) {
        try { version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json(); } catch { await sleep(200); }
    }
    if (!version) throw new Error(`Chrome did not start (${chromeBin})`);
    const ws = new WebSocket(version.webSocketDebuggerUrl);
    await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; });
    let seq = 0; const pending = new Map();
    ws.onmessage = e => { const m = JSON.parse(e.data); pending.get(m.id)?.(m); pending.delete(m.id); };
    const send = (method, params = {}, sessionId) => new Promise(r => {
        const id = ++seq; pending.set(id, r); ws.send(JSON.stringify({ id, method, params, sessionId }));
    });
    await send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: downloads });
    const { result: { targetId } } = await send('Target.createTarget', { url: 'about:blank' });
    const { result: { sessionId } } = await send('Target.attachToTarget', { targetId, flatten: true });

    for (const html of readdirSync(htmlDir).filter(f => f.endsWith('.html') && !f.includes('.visual-check'))) {
        await send('Page.navigate', { url: 'file://' + join(htmlDir, html) }, sessionId);
        await sleep(2500);
        const before = new Set(readdirSync(downloads));
        const r = await send('Runtime.evaluate', {
            expression: `Archify.exportMenu.run('svg').then(() => document.documentElement.getAttribute('data-last-export-bytes'))`,
            awaitPromise: true, returnByValue: true }, sessionId);
        let file;
        for (let i = 0; i < 25 && !file; i++) { await sleep(200); file = readdirSync(downloads).find(f => !before.has(f) && f.endsWith('.svg')); }
        if (!file) throw new Error(`no SVG exported for ${html}: ${JSON.stringify(r.result)}`);
        const dest = join(destDir, html.replace(/\.html$/, '.svg'));
        renameSync(join(downloads, file), dest);
        console.log(`svg  ${dest}  (${r.result?.result?.value} bytes)`);
    }
    ws.close();
} finally {
    chrome.kill();
    rmSync(profile, { recursive: true, force: true });
    rmSync(downloads, { recursive: true, force: true });
}
