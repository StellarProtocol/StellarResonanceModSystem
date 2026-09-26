// Opens an interactive Archify diagram full-screen over the page. Any element with
// data-diagram="/diagrams/<name>.html" (and optional data-title) is a trigger.
(() => {
  let dialog;
  function ensure() {
    if (dialog) return dialog;
    dialog = document.createElement('dialog');
    dialog.className = 'st-lightbox';
    dialog.innerHTML = '<div class="st-lb-bar"><b></b><a target="_blank" rel="noopener">Open in new tab ↗</a>' +
      '<button type="button" aria-label="Close">✕</button></div><iframe title="Interactive diagram"></iframe>';
    dialog.querySelector('button').addEventListener('click', () => dialog.close());
    dialog.addEventListener('click', (e) => { if (e.target === dialog) dialog.close(); });
    dialog.addEventListener('close', () => { dialog.querySelector('iframe').src = 'about:blank'; document.documentElement.style.overflow = ''; });
    document.body.appendChild(dialog);
    return dialog;
  }
  document.addEventListener('click', (e) => {
    const t = e.target.closest && e.target.closest('[data-diagram]');
    if (!t) return;
    e.preventDefault();
    const d = ensure();
    d.querySelector('b').textContent = t.dataset.title || 'Interactive diagram';
    d.querySelector('a').href = t.dataset.diagram;
    d.querySelector('iframe').src = t.dataset.diagram;
    document.documentElement.style.overflow = 'hidden';
    d.showModal();
  });
})();
