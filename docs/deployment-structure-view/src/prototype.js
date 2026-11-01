// Prototype wiring for the AGT-3349 Dossier. Uses DSV.render from render.js.
(function () {
  'use strict';
  const root = document.getElementById('proto');
  if (!root || !window.DSV) return;
  const canvas = document.getElementById('proto-canvas');
  const side = document.getElementById('proto-side');
  const outlineBox = document.getElementById('proto-outline');
  const S = window.DSV_SCENARIOS;
  const state = { scenario: 'agt', view: 'host', level: 2, overlay: 'health', scope: 'workspace', toggles: {}, focus: null };
  const TARGETS = {
    host: ['Host row in Settings › Environment', 'Host telemetry history', 'Header Environment view, filtered to this host'],
    server: ['Host row in Settings › Environment', 'Host telemetry history', 'Header Environment view, filtered to this host'],
    vm: ['Host row in Settings › Environment', 'Host telemetry history'],
    cluster: ['Cluster declaration', 'Deploy runs for this cluster'],
    container: ['Deploy run that placed this version (Deploy › History)', 'Logs (Deploy › Operations)', 'Runbooks of the environment'],
    process: ['Deploy or update run that placed this version', 'Logs', 'Runbooks of the environment'],
    store: ['Backup and recovery status', 'Declaration line'],
    managed: ['Environment health checks', 'Declaration line'],
    external: ['Declaration line', 'Health check, if one is declared'],
    package: ['Publish panel', 'Release page', 'Registry page'],
    artifact: ['Release page', 'Publish panel'],
  };
  const PROV = { declared: 'Declared, not observed', undeclared: 'Observed, not declared' };

  function draw(keepFocus) {
    const scn = S[state.scenario];
    const opts = { view: state.view, level: state.level, overlay: state.overlay, toggles: state.toggles, interactive: true,
      idPrefix: 'proto', scope: state.scenario === 'workspace' ? state.scope : null, focus: state.focus,
      title: `${scn.title}, ${state.view} view, level ${state.level}`, linkLevel: state.scenario === 'simple' ? 'leaf' : undefined };
    const r = window.DSV.render(scn, opts);
    canvas.innerHTML = r.svg;
    const svg = canvas.querySelector('svg');
    svg.style.minWidth = Math.min(r.width, Math.max(720, r.width * 0.8)) + 'px';
    svg.style.width = '100%';
    svg.removeAttribute('height');
    outlineBox.innerHTML = window.DSV.outline(r.tree);
    sync();
    if (keepFocus) {
      const el = canvas.querySelector(`[data-id="${CSS.escape(keepFocus)}"], [data-leaf="${CSS.escape(keepFocus)}"]`);
      if (el) el.focus({ preventScroll: true });
    }
  }
  function sync() {
    root.querySelectorAll('fieldset[data-control]').forEach(fs => {
      const key = fs.dataset.control;
      fs.querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', String(String(state[key]) === b.dataset.value)));
    });
    root.querySelector('fieldset[data-control="scope"]').hidden = state.scenario !== 'workspace';
  }
  function select(id) {
    const n = S[state.scenario].nodes.find(x => x.id === id);
    state.focus = id;
    draw(id);
    if (!n) return;
    const links = (S[state.scenario].links || []).filter(l => l.from === id || l.to === id);
    const label = id => (S[state.scenario].nodes.find(x => x.id === id) || {}).label || id;
    side.innerHTML = `<h4>${esc(n.label)}</h4><dl><dt>Kind</dt><dd>${esc(window.DSV.KIND_LABEL[n.kind] || n.kind)}</dd>`
      + (n.status ? `<dt>Status</dt><dd>${esc(window.DSV.STATUS_WORD[n.status])}</dd>` : '')
      + (n.ver ? `<dt>Version</dt><dd>${esc(n.ver)}${n.behind ? ', behind the current release' : ''}</dd>` : '')
      + `<dt>Facts</dt><dd>${esc(PROV[n.prov] || 'Declared and observed')}</dd>`
      + (n.drift ? `<dt>Drift</dt><dd>${esc(n.drift)}</dd>` : '')
      + `</dl><b>Links</b>${links.length ? `<ul>${links.map(l => `<li>${esc(l.label || l.kind)}: ${esc(label(l.from))} → ${esc(label(l.to))}</li>`).join('')}</ul>` : '<p>None declared.</p>'}`
      + `<b>Click leads to</b><ul>${(TARGETS[n.kind] || ['Declaration line']).map(t => `<li>${esc(t)}</li>`).join('')}</ul>`;
  }
  function clear() {
    state.focus = null;
    side.innerHTML = '<h4>Selection</h4><p>Select an instance to see its links and click targets.</p>';
    draw();
  }
  const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

  root.querySelector('.proto-bar').addEventListener('click', e => {
    const b = e.target.closest('button[data-value]');
    if (!b) return;
    const key = b.closest('fieldset').dataset.control;
    state[key] = key === 'level' ? Number(b.dataset.value) : b.dataset.value;
    if (key !== 'overlay') state.toggles = {};
    if (key === 'scenario') { state.scope = 'workspace'; state.focus = null; state.level = b.dataset.value === 'complex' ? 1 : 2; clear(); canvas.scrollTo(0, 0); return; }
    draw();
  });
  function activate(el) {
    if (el.dataset.id) {
      const open = el.getAttribute('aria-expanded') === 'true';
      state.toggles[el.dataset.id] = open;
      draw(el.dataset.id);
    } else if (el.dataset.leaf) select(el.dataset.leaf);
  }
  canvas.addEventListener('click', e => {
    const el = e.target.closest('[data-id], [data-leaf]');
    if (el) activate(el);
  });
  canvas.addEventListener('keydown', e => {
    const el = e.target.closest('[data-id], [data-leaf]');
    if ((e.key === 'Enter' || e.key === ' ') && el) { e.preventDefault(); activate(el); return; }
    if (['1', '2', '3'].includes(e.key)) { state.level = Number(e.key); state.toggles = {}; draw(); canvas.querySelector('[tabindex]')?.focus(); }
    else if (e.key === 'h' || e.key === 'c') { state.view = e.key === 'h' ? 'host' : 'container'; state.toggles = {}; draw(); canvas.querySelector('[tabindex]')?.focus(); }
    else if (e.key === 'Escape') { clear(); }
  });
  draw();
})();
