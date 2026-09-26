// Version menu — re-renders the header's version switcher from PRODUCTION's /versions.json so every page
// (including frozen release snapshots and a production build that predates the newest tag) shows the current list.
// The server-rendered menu (baked at build time) stays as the fallback when the fetch fails.
(() => {
  const root = document.querySelector('[data-st-version]');
  if (!root) return;
  const LIVE = root.dataset.live || 'https://docs.stellarresonance.app/versions.json';
  const baked = JSON.parse(root.querySelector('script[type="application/json"]').textContent);
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const cmp = (a, b) => { const x = a.split('.').map(Number), y = b.split('.').map(Number); for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i]; return 0; };

  function render(live) {
    const me = baked.build; // what THIS page is — always from the baked copy
    const items = [];
    const mainLabel = live.main.ahead
      ? `main <small>in development · ${esc(live.main.version)}</small>`
      : `${esc(live.main.version)} <small>latest release</small>`;
    items.push({ href: live.main.url, html: mainLabel, current: me.kind === 'main' });
    for (const s of live.snapshots) {
      const latest = s.version === live.latestRelease;
      if (!live.main.ahead && latest) continue; // production already IS the latest release
      items.push({ href: s.url, html: `${esc(s.version)} <small>${latest ? 'latest release' : 'snapshot'}</small>`,
        current: me.kind === 'snapshot' && me.version === s.version });
    }
    const menu = root.querySelector('.menu');
    menu.innerHTML = items.map((i) => `<a href="${esc(i.href)}"${i.current ? ' aria-current="page"' : ''}>${i.html}</a>`).join('') +
      (live.snapshots.length === 0 ? '<span class="empty">Release snapshots start with the next framework release.</span>' : '') +
      '<a href="/changelog/" class="all">Changelog →</a>';
    const pill = root.querySelector('summary');
    const old = me.kind === 'snapshot' && live.latestRelease && cmp(me.version, live.latestRelease) < 0;
    pill.innerHTML = me.kind === 'snapshot' ? `Version <b>${esc(me.version)}</b>${old ? ' <em>old</em>' : ''}`
      : me.kind === 'preview' ? `Preview <b>${esc(me.version)}</b>`
      : live.main.ahead ? `Version <b>main</b>` : `Version <b>${esc(me.version)}</b>`;
    root.classList.toggle('is-old', !!old);
  }

  render(baked);
  fetch(LIVE, { cache: 'no-cache' }).then((r) => (r.ok ? r.json() : null)).then((live) => live && render(live)).catch(() => {});
})();
