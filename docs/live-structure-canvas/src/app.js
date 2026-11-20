// Prototype controller for the AGT-3955 Dossier: layers, personas, replay of
// recorded deltas (batched to one visual update a second), minimap, search,
// keyboard navigation and the outline that is the canvas's text alternative.
(function () {
  'use strict';
  const E = window.LSCEngine, M = window.LSCModel, L = window.LSCLayers, S = window.LSCSpec;
  const host = document.getElementById('lsc');
  if (!host || !E) return;
  const $ = sel => host.querySelector(sel);
  const el = (tag, attrs, ...kids) => {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v == null || v === false) continue;
      if (k === 'class') e.className = v; else if (k === 'text') e.textContent = v; else if (k.startsWith('on')) e.addEventListener(k.slice(2), v); else e.setAttribute(k, v === true ? '' : v);
    }
    for (const c of kids.flat()) if (c != null) e.append(c.nodeType ? c : document.createTextNode(String(c)));
    return e;
  };
  const clone = v => JSON.parse(JSON.stringify(v));
  const reduce = window.matchMedia('(prefers-reduced-motion: reduce)');
  const dark = window.matchMedia('(prefers-color-scheme: dark)');

  // ---------- state ----------
  const baseline = { obs: clone(M.obs), volumes: clone(M.volumes), warnings: clone(M.warnings), inside: clone(M.inside), links: clone(M.links) };
  const state = {
    layers: new Set(['perf', 'health', 'warnings']), motion: reduce.matches ? 'off' : 'lively', iso: false,
    persona: 'operations-warden', spec: null, specErrors: null, specOverride: null, latest: {},
    selectedId: null, facts: null, scene: null, T: {},
    requestRedraw: () => engine.invalidate(),
    clock: 0, playing: false, speed: 1, cursor: 0, pending: [], received: 0, updates: 0, log: [],
  };
  function resetFacts() {
    state.facts = { obs: clone(baseline.obs), volumes: clone(baseline.volumes), dataLocations: M.dataLocations, warnings: clone(baseline.warnings), inside: clone(baseline.inside) };
    state.scene = E.buildScene({ nodes: M.nodes, links: clone(baseline.links) });
  }
  resetFacts();
  const canvas = $('#lsc-canvas'), wrap = $('#lsc-wrap'), mini = $('#lsc-minimap');
  const painter = L.createPainter(state);
  const engine = E.createEngine(canvas, { hooks: painter.hooks, motion: state.motion });
  painter.reindex();
  engine.setScene(state.scene);

  // ---------- theme ----------
  function readTokens() {
    const cs = getComputedStyle(host);
    // Resolve each token to an rgb() string the canvas understands, including color-mix().
    const probe = document.createElement('span');
    probe.style.display = 'none'; host.append(probe);
    const v = n => { probe.style.color = 'var(--lsc-' + n + ')'; return getComputedStyle(probe).color; };
    state.T = {
      paper: v('paper'), surface: v('surface'), surfaceSoft: v('surface-soft'), surfaceStrong: v('surface-strong'), ink: v('ink'), muted: v('muted'), faint: v('faint'),
      line: v('line'), link: v('link'), good: v('good'), warn: v('warn'), bad: v('bad'), goodSoft: v('good-soft'), warnSoft: v('warn-soft'), badSoft: v('bad-soft'),
      sideA: v('side-a'), sideB: v('side-b'), coding: v('coding'), gate: v('gate'), review: v('review'), font: cs.fontFamily,
    };
    probe.remove();
    engine.setTokens(state.T);
    drawMinimap();
  }
  dark.addEventListener('change', readTokens);

  // ---------- assessment ----------
  const factsIndex = () => ({ nodes: new Set(M.nodes.map(n => n.id)), links: new Set(state.scene.links.map(l => l.id)), warnings: new Set(state.facts.warnings.map(w => w.id)) });
  function applyPersona() {
    const p = M.personas.find(x => x.id === state.persona);
    let spec = null, errors = null;
    if (p && p.spec) {
      const raw = state.specOverride || state.latest[p.id] || M.specs[p.spec];
      const res = S.validate(raw, factsIndex());
      if (res.ok) spec = raw; else errors = res.errors;
      state.raw = raw;
    } else state.raw = null;
    state.spec = spec; state.specErrors = errors;
    renderAssessment(); renderWarnings(); renderDetail(); renderOutline();
    engine.invalidate();
  }

  // ---------- controls ----------
  function pressed(group, value) {
    for (const b of host.querySelectorAll(`[data-control="${group}"] button`)) b.setAttribute('aria-pressed', String(b.dataset.value === value));
  }
  function buildControls() {
    const lay = $('[data-control="layers"]');
    for (const l of L.LAYERS) {
      lay.append(el('button', { type: 'button', 'data-value': l.id, 'aria-pressed': String(state.layers.has(l.id)), title: l.data,
        onclick: () => { toggleLayer(l.id); } }, l.title.replace('Construction sites and warnings', 'Sites and warnings').replace('Performance and network', 'Performance, network')));
    }
    const per = $('[data-control="persona"]');
    for (const p of M.personas) per.append(el('button', { type: 'button', 'data-value': p.id, 'aria-pressed': String(p.id === state.persona), onclick: () => { state.persona = p.id; state.specOverride = null; pressed('persona', p.id); applyPersona(); } }, p.title));
    for (const b of host.querySelectorAll('[data-control="view"] button')) b.addEventListener('click', () => setIso(b.dataset.value === 'iso'));
    for (const b of host.querySelectorAll('[data-control="motion"] button')) b.addEventListener('click', () => setMotion(b.dataset.value));
    pressed('motion', state.motion); pressed('view', 'flat');
  }
  function toggleLayer(id, force) {
    const on = force == null ? !state.layers.has(id) : force;
    if (on) state.layers.add(id); else state.layers.delete(id);
    for (const b of host.querySelectorAll('[data-control="layers"] button')) b.setAttribute('aria-pressed', String(state.layers.has(b.dataset.value)));
    renderLegend(); renderOutline(); renderDetail(); engine.invalidate();
  }
  function setIso(on) { state.iso = on; pressed('view', on ? 'iso' : 'flat'); engine.setIso(on); setTimeout(() => engine.fit(state.motion === 'off' ? 0 : 520), state.motion === 'off' ? 0 : 720); }
  function setMotion(m) { state.motion = m; engine.setMotion(m); pressed('motion', m); }

  // ---------- legend ----------
  function swatch(kind, tone, dash) {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('width', '26'); svg.setAttribute('height', '14'); svg.setAttribute('aria-hidden', 'true');
    const c = `var(--lsc-${tone})`;
    const add = (tag, a) => { const e = document.createElementNS(ns, tag); for (const [k, v] of Object.entries(a)) e.setAttribute(k, v); svg.append(e); return e; };
    if (kind === 'bar') { add('rect', { x: 1, y: 5, width: 24, height: 5, fill: 'var(--lsc-surface-strong)' }); add('rect', { x: 1, y: 5, width: 15, height: 5, fill: c }); }
    else if (kind === 'dots') for (let i = 0; i < 4; i++) add('circle', { cx: 3 + i * 6.5, cy: 7, r: 2, fill: c });
    else if (kind === 'line') add('line', { x1: 1, y1: 7, x2: 25, y2: 7, stroke: c, 'stroke-width': 1.8, 'stroke-dasharray': (dash || []).join(' ') });
    else if (kind === 'broken') { add('line', { x1: 1, y1: 7, x2: 10, y2: 7, stroke: c, 'stroke-width': 1.8 }); add('line', { x1: 16, y1: 7, x2: 25, y2: 7, stroke: c, 'stroke-width': 1.8 }); add('path', { d: 'M11 4 L15 10 M15 4 L11 10', stroke: c, 'stroke-width': 1.6 }); }
    else if (kind === 'shield') add('path', { d: 'M13 1 L19 3.5 L19 7.5 Q18 11.5 13 13 Q8 11.5 7 7.5 L7 3.5 Z', fill: tone === 'muted' || tone === 'faint' ? 'none' : c, stroke: c, 'stroke-width': 1.2, 'stroke-dasharray': tone === 'faint' ? '2 2' : '' });
    else if (kind === 'pin') { add('path', { d: 'M13 1 L18.5 11 L7.5 11 Z', fill: c }); add('rect', { x: 6, y: 10.5, width: 14, height: 2.5, fill: c }); }
    else if (kind === 'dot') add('circle', { cx: 13, cy: 7, r: 4, fill: c });
    else if (kind === 'ring') add('circle', { cx: 13, cy: 7, r: 3.6, fill: 'none', stroke: c, 'stroke-width': 1.4 });
    else if (kind === 'chip') add('rect', { x: 2, y: 3, width: 22, height: 8, rx: 4, fill: tone === 'warn' ? 'var(--lsc-warn-soft)' : 'var(--lsc-surface-strong)', stroke: c, 'stroke-width': 0.8 });
    else if (kind === 'outline') add('rect', { x: 2, y: 2, width: 22, height: 10, rx: 3, fill: 'none', stroke: c, 'stroke-width': 1.4, 'stroke-dasharray': (dash || []).join(' ') });
    else if (kind === 'text') { const t = add('text', { x: 1, y: 11, 'font-size': 9, fill: c }); t.textContent = '21 d'; }
    return svg;
  }
  function renderLegend() {
    const box = $('#lsc-legend');
    box.replaceChildren();
    for (const l of L.LAYERS) {
      if (!state.layers.has(l.id)) continue;
      box.append(el('div', { class: 'lsc-legend-layer', 'data-layer': l.id }, el('b', { text: l.title }),
        el('ul', null, l.legend.map(([kind, tone, label, dash]) => el('li', null, swatch(kind, tone, dash), label)))));
    }
    if (!state.layers.has('backup')) box.append(el('div', { class: 'lsc-legend-layer' }, el('b', { text: 'Always shown' }), el('ul', null, el('li', null, swatch('shield', 'bad'), 'Missing backup'))));
    box.append(el('div', { class: 'lsc-legend-layer' }, el('b', { text: 'Work in motion' }), el('ul', null,
      ['coding', 'gate', 'review'].map(k => el('li', null, swatch('dot', k), k[0].toUpperCase() + k.slice(1))))));
  }

  // ---------- panels ----------
  const fmtTime = iso => new Date(iso).toISOString().slice(11, 16);
  const nowIso = () => new Date(Date.parse(M.AS_OF) + state.clock * 1000).toISOString();
  function ago(iso) {
    const m = Math.max(0, Math.round((Date.parse(nowIso()) - Date.parse(iso)) / 60000));
    return m < 1 ? 'just now' : m === 1 ? '1 minute ago' : m + ' minutes ago';
  }
  function renderAssessment() {
    const box = $('#lsc-assessment');
    box.replaceChildren();
    const p = M.personas.find(x => x.id === state.persona);
    if (!p || !p.spec) {
      box.append(el('h4', { text: 'Facts only' }), el('p', { class: 'lsc-dim', text: 'No assessment is shown. Every mark on the canvas comes from the APIs. Pick a persona to see its judgement, callouts and next steps.' }));
      return;
    }
    if (state.specErrors) {
      box.append(el('h4', { text: p.title }), el('div', { class: 'lsc-reject', role: 'status' },
        el('b', { text: 'Assessment rejected; showing facts only.' }), el('ul', null, state.specErrors.slice(0, 4).map(e => el('li', { text: e })))));
      box.append(el('button', { type: 'button', class: 'lsc-link', onclick: () => { state.specOverride = null; applyPersona(); } }, 'Use the last valid specification'));
      return;
    }
    const s = state.spec;
    box.append(
      el('h4', null, s.persona.title, el('span', { class: 'lsc-role', text: s.persona.role + ' role' })),
      el('p', { class: 'lsc-prov' }, `Assessed ${fmtTime(s.createdAt)} UTC, ${ago(s.createdAt)} · revision ${s.revision} · ${s.model.id || s.model.capabilityClass} · confidence ${Math.round(s.confidence * 100)} %` + (s.trigger ? ' · trigger: ' + s.trigger : '')),
      el('p', { class: 'lsc-headline', text: s.headline }),
      s.judgement ? el('p', { class: 'lsc-judgement', text: s.judgement }) : null,
    );
    if (s.annotations && s.annotations.length) {
      box.append(el('ol', { class: 'lsc-callouts', 'aria-label': 'Callouts on the canvas' }, s.annotations.map(a => el('li', { 'data-tone': a.tone },
        el('button', { type: 'button', class: 'lsc-link', onclick: () => { const id = a.node || (state.scene.links.find(l => l.id === a.link) || {}).from; if (id) focusNode(id, true); } }, a.title),
        a.text ? ' ' + a.text : ''))));
    }
    for (const g of s.goals || []) {
      const pct = Math.round(g.done / g.total * 100);
      box.append(el('div', { class: 'lsc-goal' },
        el('div', { class: 'lsc-goal-head' }, el('b', { text: g.title }), el('span', { text: `${g.done} of ${g.total}` })),
        el('div', { class: 'lsc-goal-bar', role: 'progressbar', 'aria-valuemin': 0, 'aria-valuemax': g.total, 'aria-valuenow': g.done, 'aria-label': g.title }, el('span', { style: `width: ${pct}%` })),
        el('ol', { class: 'lsc-steps' }, (g.steps || []).map(st => el('li', null, el('span', { class: 'lsc-kind', 'data-kind': st.kind, text: st.kind.replace('-', ' ') }), el('b', { text: st.title }), st.detail ? el('small', { text: st.detail }) : null)))));
    }
    if (s.layers && s.layers.enable) {
      const missing = s.layers.enable.filter(x => !state.layers.has(x));
      if (missing.length) box.append(el('button', { type: 'button', class: 'lsc-link', onclick: () => missing.forEach(x => toggleLayer(x, true)) }, 'Show suggested layers: ' + missing.map(x => L.LAYERS.find(l => l.id === x).title).join(', ')));
    }
    box.append(el('details', { class: 'lsc-facts' }, el('summary', { text: `Facts used (${s.factsUsed.length}) and specification` }),
      el('ul', null, s.factsUsed.map(f => el('li', null, el('code', { text: f })))),
      el('pre', null, el('code', { text: JSON.stringify(state.raw, null, 1).slice(0, 2400) })),
      el('button', { type: 'button', class: 'lsc-link', 'data-action': 'invalid-spec', onclick: () => { state.specOverride = M.specs.invalid; applyPersona(); } }, 'Try an invalid specification')));
  }
  const nodeLabel = id => (state.scene.byId.get(id) || {}).label || id;
  function rankedWarnings() {
    const rank = state.spec && state.spec.ranking;
    const sev = { bad: 0, warn: 1, info: 2 };
    return [...state.facts.warnings].sort((a, b) => {
      if (rank) { const ra = rank.indexOf(a.id), rb = rank.indexOf(b.id); if (ra !== rb) return (ra < 0 ? 99 : ra) - (rb < 0 ? 99 : rb); }
      return sev[a.severity] - sev[b.severity];
    });
  }
  function renderWarnings() {
    const box = $('#lsc-warnings');
    const list = rankedWarnings();
    box.replaceChildren(el('h4', null, 'Construction sites and warnings ', el('span', { class: 'lsc-count', text: String(list.length) })),
      el('p', { class: 'lsc-dim', text: state.spec ? 'Ranked by ' + state.spec.persona.title.toLowerCase() + '.' : 'Ordered by severity; pick a persona to rank them.' }),
      el('ol', { class: 'lsc-warn-list' }, list.map(w => el('li', { 'data-severity': w.severity },
        el('button', { type: 'button', class: 'lsc-warn', onclick: () => focusNode(w.node, true) },
          el('span', { class: 'lsc-cone', 'data-severity': w.severity, 'aria-hidden': 'true' }),
          el('span', null, el('b', { text: w.title }), el('small', { text: nodeLabel(w.node) + ' · ' + w.what }),
            el('small', null, el('em', { text: 'Blocks: ' }), w.blocks), el('small', null, el('em', { text: 'Next: ' }), w.next)))))));
  }
  function pathOf(n) { const out = []; for (let p = n.parentNode; p; p = p.parentNode) out.unshift(p.label); return out.join(' › '); }
  function renderDetail() {
    const box = $('#lsc-detail');
    const n = state.selectedId && state.scene.byId.get(state.selectedId);
    if (!n) { box.replaceChildren(el('h4', { text: 'Selection' }), el('p', { class: 'lsc-dim', text: 'Select a node on the canvas, in the outline or by search to see its facts per layer.' })); return; }
    const o = state.facts.obs[n.id] || {};
    const rows = [];
    const add = (k, v, cls) => rows.push(el('tr', { class: cls }, el('th', { text: k }), el('td', { text: v })));
    add('Kind', (n.sub || n.kind) + (pathOf(n) ? ' · on ' + pathOf(n) : ''));
    if (o.health) add('Health', { ok: 'Healthy', warn: 'Degraded', bad: 'Down', unknown: 'Not observed' }[o.health] || o.health);
    if (o.cpu != null) add('CPU and memory', o.cpu + ' %' + (o.mem ? ' · ' + o.mem[0] + ' of ' + o.mem[1] + ' GB' : '') + (o.load != null ? ' · load ' + o.load + ' of ' + o.cores : ''));
    if (o.slots) add('Slots', o.slots[0] + ' of ' + o.slots[1] + ' busy');
    if (o.release) add('Release', o.release + (o.behind ? ', ' + o.behind + ' behind stable ' + o.expected : ', on stable') + (o.pendingInstall ? ', install pending' : ''));
    if (o.difference) add('Drift', o.difference);
    for (const v of state.facts.volumes[n.id] || []) { const d = L.daysToFull(v); add('Volume', `${v.label}: ${v.usedPct} % of ${v.sizeGb} GB, +${v.growthGbPerDay} GB a day` + (d ? `, full in about ${d} days` : '')); }
    for (const l of M.dataLocations.filter(x => x.node === n.id)) {
      const s = L.locStatus(l);
      const copies = l.copies.length ? l.copies.map(c => c.kind + (c.at ? ' ' + c.at.slice(0, 10) : '') + (c.offHost ? '' : ', same disk')).join('; ') : 'none';
      add('Data', `${l.label} (${L.gb(l.sizeGb)}): ${L.STATUS_WORD[s]}. Copies: ${copies}.`, 'lsc-row-' + L.STATUS_TONE[s]);
    }
    for (const w of state.facts.warnings.filter(x => x.node === n.id)) add('Problem', `${w.title}. Blocks: ${w.blocks} Next: ${w.next}`, 'lsc-row-' + w.severity);
    const links = state.scene.links.filter(l => !l.layer && (l.from === n.id || l.to === n.id));
    for (const l of links) add('Link', `${l.label} to ${nodeLabel(l.from === n.id ? l.to : l.from)}: ${l.state === 'down' ? 'down' : (l.latencyMs || 0) + ' ms, ' + L.fmtBytes(l.traffic || 0)}`, l.state === 'down' ? 'lsc-row-bad' : '');
    const ann = state.spec && (state.spec.annotations || []).filter(a => a.node === n.id);
    box.replaceChildren(el('h4', { text: n.label }),
      ann && ann.length ? el('div', { class: 'lsc-ann' }, ann.map(a => el('p', null, el('b', { text: a.title + '. ' }), a.text || ''))) : null,
      el('table', { class: 'lsc-facts-table' }, el('tbody', null, rows)));
  }

  // ---------- outline (text alternative) ----------
  function describe(n) {
    const o = state.facts.obs[n.id] || {};
    const parts = [n.label, n.sub || n.kind];
    if (state.layers.has('health') && o.health) parts.push({ ok: 'healthy', warn: 'degraded', bad: 'down', unknown: 'not observed' }[o.health]);
    if (state.layers.has('perf') && o.cpu != null) parts.push('CPU ' + o.cpu + ' %' + (o.slots ? ', slots ' + o.slots[0] + ' of ' + o.slots[1] : ''));
    if (state.layers.has('storage')) for (const v of state.facts.volumes[n.id] || []) parts.push(`${v.label} ${v.usedPct} % used`);
    if (state.layers.has('version') && o.release) parts.push(o.release + (o.behind ? ', behind stable' : ''));
    if (state.layers.has('drift') && o.difference) parts.push(o.difference);
    const b = painter.backupOf(n, false);
    if (b && (state.layers.has('backup') || b.worst === 'gap')) parts.push('backup: ' + (b.must ? (b.must - b.copied) + ' of ' + b.must + ' must-keep without copy' : L.STATUS_WORD[b.worst]));
    const ws = state.facts.warnings.filter(w => w.node === n.id);
    if (state.layers.has('warnings') && ws.length) parts.push(ws.length + (ws.length === 1 ? ' problem: ' : ' problems: ') + ws.map(w => w.title).join('; '));
    return parts.join(', ');
  }
  function renderOutline() {
    const box = $('#lsc-outline');
    const build = (list, level) => el('ul', { role: level === 1 ? 'tree' : 'group', 'aria-label': level === 1 ? 'Structure outline' : null },
      list.map(n => el('li', { role: 'treeitem', id: 'lsc-o-' + n.id, 'aria-level': level, 'aria-selected': String(state.selectedId === n.id), 'aria-expanded': n.children.length ? 'true' : null, 'data-id': n.id },
        el('button', { type: 'button', tabindex: '-1', onclick: () => focusNode(n.id, true), text: describe(n) }), n.children.length ? build(n.children, level + 1) : null)));
    const links = state.scene.links.filter(l => !l.layer);
    box.replaceChildren(build(state.scene.root.children, 1),
      el('h5', { text: 'Links' }), el('ul', { class: 'lsc-links' }, links.map(l => el('li', { text: `${nodeLabel(l.from)} to ${nodeLabel(l.to)}: ${l.label}, ${l.state === 'down' ? 'down' : (l.latencyMs || 0) + ' ms, ' + L.fmtBytes(l.traffic || 0)}` }))));
  }

  // ---------- selection and focus ----------
  function select(id) {
    state.selectedId = id;
    renderDetail();
    for (const li of host.querySelectorAll('#lsc-outline [role="treeitem"]')) li.setAttribute('aria-selected', String(li.dataset.id === id));
    engine.invalidate();
  }
  function setFocus(id) {
    engine.st.focusId = id;
    if (id) wrap.setAttribute('aria-activedescendant', 'lsc-o-' + id); else wrap.removeAttribute('aria-activedescendant');
    const n = id && state.scene.byId.get(id);
    $('#lsc-announce').textContent = n ? describe(n) : '';
    engine.request();
  }
  function focusNode(id, fly) {
    const n = state.scene.byId.get(id);
    if (!n) return;
    setFocus(id); select(id);
    if (fly) {
      // Fly so the node is large enough to show its inside.
      const fill = n.kind === 'container' ? 0.55 : n.kind === 'external' || n.kind === 'target' ? 0.3 : 0.8;
      engine.focusRect(n, fill, state.motion === 'off' ? 0 : 620);
    }
  }

  // ---------- levels ----------
  const LEVEL_WORDS = { 1: 'Sites and outside services', 2: 'Machines, physical and VM', 3: 'Containers, services and daemons', 4: 'Inside a service' };
  function goLevel(lv) {
    const sc = state.scene, b = sc.bounds;
    const fit = engine.cameraFor(b, 0.94);
    const sites = sc.root.children, machines = sc.nodes.filter(n => n.kind === 'machine' && !n.openAt);
    let k;
    if (lv === 1) k = Math.min(fit.k, 0.85 * Math.min(...sites.map(n => E.OPEN_AT[n.kind] / n.w)));
    else if (lv === 2) k = Math.min(Math.max(fit.k, 1.4 * Math.max(...sites.map(n => E.OPEN_AT[n.kind] / n.w))), 0.88 * Math.min(...machines.map(n => E.OPEN_AT.machine / n.w)));
    else if (lv === 3) k = Math.max(fit.k, 1.45 * Math.max(...sc.nodes.filter(n => n.kind === 'machine' || n.kind === 'vm').map(n => (n.openAt || E.OPEN_AT[n.kind]) / n.w)));
    if (lv === 4) {
      const target = state.scene.byId.get(state.selectedId) || state.scene.byId.get('c-runner-a');
      const c = target.kind === 'container' ? target : state.scene.byId.get('c-runner-a');
      focusNode(c.id, false);
      const cam = engine.cameraFor(c, 0.5);
      cam.k = Math.max(cam.k, 1.5 * E.OPEN_AT.container / c.w);
      const cx = c.x + c.w / 2, cy = c.y + c.h / 2, p = engine.proj.p(cx, cy, c.z);
      engine.animateTo({ k: cam.k, tx: engine.st.w / 2 - p[0] * cam.k, ty: engine.st.h / 2 - p[1] * cam.k }, state.motion === 'off' ? 0 : 620);
      return;
    }
    const pb = engine.cameraFor(b, 0.94);
    // Keep the scene's centre; zoom to the level's scale.
    const cx = (engine.st.w / 2 - pb.tx) / pb.k, cy = (engine.st.h / 2 - pb.ty) / pb.k;
    engine.animateTo({ k, tx: engine.st.w / 2 - cx * k, ty: engine.st.h / 2 - cy * k }, state.motion === 'off' ? 0 : 620);
  }
  function updateLevel() {
    const lv = engine.level();
    $('#lsc-level').textContent = 'Level ' + lv + ' · ' + LEVEL_WORDS[lv];
    for (const b of host.querySelectorAll('[data-control="level"] button')) b.setAttribute('aria-pressed', String(+b.dataset.value === lv));
  }

  // ---------- minimap ----------
  function drawMinimap() {
    if (!mini) return;
    const ctx = mini.getContext('2d'), w = mini.clientWidth || 160, h = mini.clientHeight || 100, dpr = Math.min(2, window.devicePixelRatio || 1);
    if (mini.width !== Math.round(w * dpr)) { mini.width = Math.round(w * dpr); mini.height = Math.round(h * dpr); }
    const b = state.scene.bounds, s = Math.min(w / b.w, h / b.h) * 0.92, ox = (w - b.w * s) / 2, oy = (h - b.h * s) / 2;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = state.T.surface; ctx.fillRect(0, 0, w, h);
    for (const n of state.scene.nodes) {
      if (n.depth > 2) continue;
      ctx.fillStyle = n.depth === 0 ? state.T.surfaceStrong : state.T.paper;
      ctx.strokeStyle = state.T.line; ctx.lineWidth = 0.5;
      ctx.fillRect(ox + n.x * s, oy + n.y * s, n.w * s, n.h * s); ctx.strokeRect(ox + n.x * s, oy + n.y * s, n.w * s, n.h * s);
    }
    for (const wn of state.facts.warnings) {
      const n = state.scene.byId.get(wn.node); if (!n) continue;
      ctx.fillStyle = wn.severity === 'bad' ? state.T.bad : state.T.warn;
      ctx.beginPath(); ctx.arc(ox + (n.x + n.w - 12) * s, oy + (n.y + 10) * s, 2, 0, Math.PI * 2); ctx.fill();
    }
    // The viewport on the ground plane: a rectangle in 2D, a rotated quad in 2.5D.
    const corners = [[0, 0], [engine.st.w, 0], [engine.st.w, engine.st.h], [0, engine.st.h]].map(([x, y]) => engine.toWorld(x, y));
    ctx.strokeStyle = state.T.link; ctx.lineWidth = 1.5;
    ctx.beginPath();
    corners.forEach(([x, y], i) => (i ? ctx.lineTo(ox + x * s, oy + y * s) : ctx.moveTo(ox + x * s, oy + y * s)));
    ctx.closePath(); ctx.stroke();
    mini.dataset.scale = s; mini.dataset.ox = ox; mini.dataset.oy = oy;
  }
  function miniJump(ev) {
    const r = mini.getBoundingClientRect(), s = +mini.dataset.scale;
    const wx = (ev.clientX - r.left - +mini.dataset.ox) / s, wy = (ev.clientY - r.top - +mini.dataset.oy) / s;
    const p = engine.proj.p(wx, wy, 0);
    engine.animateTo({ k: engine.st.k, tx: engine.st.w / 2 - p[0] * engine.st.k, ty: engine.st.h / 2 - p[1] * engine.st.k }, ev.type === 'pointermove' || state.motion === 'off' ? 0 : 300);
  }
  if (mini) {
    let drag = false;
    mini.addEventListener('pointerdown', e => { drag = true; mini.setPointerCapture(e.pointerId); miniJump(e); });
    mini.addEventListener('pointermove', e => { if (drag) miniJump(e); });
    mini.addEventListener('pointerup', () => { drag = false; });
  }
  let cameraQueued = false;
  engine.on('camera', () => {
    if (cameraQueued) return;
    cameraQueued = true;
    requestAnimationFrame(() => { cameraQueued = false; updateLevel(); drawMinimap(); });
  });

  // ---------- pointer: pan, wheel zoom, pinch, select ----------
  const pointers = new Map();
  let dragMoved = false, pinch0 = null;
  canvas.addEventListener('pointerdown', e => {
    canvas.setPointerCapture(e.pointerId);
    pointers.set(e.pointerId, { x: e.offsetX, y: e.offsetY });
    dragMoved = false;
    if (pointers.size === 2) { const [a, b] = [...pointers.values()]; pinch0 = { d: Math.hypot(a.x - b.x, a.y - b.y), cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2 }; }
  });
  canvas.addEventListener('pointermove', e => {
    const p = pointers.get(e.pointerId);
    if (!p) {
      const n = engine.hit(e.offsetX, e.offsetY);
      canvas.style.cursor = n ? 'pointer' : 'grab';
      return;
    }
    const dx = e.offsetX - p.x, dy = e.offsetY - p.y;
    p.x = e.offsetX; p.y = e.offsetY;
    if (pointers.size === 2 && pinch0) {
      const [a, b] = [...pointers.values()];
      const d = Math.hypot(a.x - b.x, a.y - b.y), cx = (a.x + b.x) / 2, cy = (a.y + b.y) / 2;
      engine.zoomAt(cx, cy, d / pinch0.d); engine.panBy(cx - pinch0.cx, cy - pinch0.cy);
      pinch0 = { d, cx, cy };
      dragMoved = true;
      return;
    }
    if (Math.abs(dx) + Math.abs(dy) > 0) { if (Math.hypot(dx, dy) > 1) dragMoved = true; engine.panBy(dx, dy); canvas.style.cursor = 'grabbing'; }
  });
  const up = e => {
    const wasPinch = pointers.size > 1;
    pointers.delete(e.pointerId);
    if (pointers.size < 2) pinch0 = null;
    if (!dragMoved && !wasPinch && e.type === 'pointerup') {
      const n = engine.hit(e.offsetX, e.offsetY);
      if (n) { setFocus(n.id); select(n.id); } else { select(null); setFocus(null); }
    }
  };
  canvas.addEventListener('pointerup', up);
  canvas.addEventListener('pointercancel', up);
  canvas.addEventListener('wheel', e => {
    e.preventDefault();
    const f = Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0016));
    engine.zoomAt(e.offsetX, e.offsetY, f);
  }, { passive: false });
  canvas.addEventListener('dblclick', e => { const n = engine.hit(e.offsetX, e.offsetY); if (n) focusNode(n.id, true); });

  // ---------- keyboard ----------
  function visibleSiblings(n) {
    const list = n.parentNode ? n.parentNode.children : state.scene.root.children;
    return list;
  }
  wrap.addEventListener('focus', () => { if (!engine.st.focusId) setFocus(state.selectedId || state.scene.root.children[1].id); });
  wrap.addEventListener('keydown', e => {
    if (e.target !== wrap) return;
    const k = e.key, f = engine.st.focusId && state.scene.byId.get(engine.st.focusId);
    const step = 60;
    let handled = true;
    if (e.shiftKey && k.startsWith('Arrow')) engine.panBy(k === 'ArrowLeft' ? step : k === 'ArrowRight' ? -step : 0, k === 'ArrowUp' ? step : k === 'ArrowDown' ? -step : 0);
    else if (k.startsWith('Arrow') && f) {
      let next = null;
      const sib = visibleSiblings(f), i = sib.indexOf(f);
      if (k === 'ArrowRight') next = sib[(i + 1) % sib.length];
      else if (k === 'ArrowLeft') next = sib[(i - 1 + sib.length) % sib.length];
      else if (k === 'ArrowDown' && f.children.length) next = f.children[0];
      else if (k === 'ArrowUp' && f.parentNode) next = f.parentNode;
      if (next) {
        setFocus(next.id);
        // Keep the focused node on screen and large enough to read.
        const r = engine.rep(next);
        if (r !== next || !next.q || next.q[0][0] < 0 || next.q[2][0] > engine.st.w || next.q[0][1] < 0 || next.q[2][1] > engine.st.h) focusNode(next.id, true);
      }
    } else if (k.startsWith('Arrow')) engine.panBy(k === 'ArrowLeft' ? step : k === 'ArrowRight' ? -step : 0, k === 'ArrowUp' ? step : k === 'ArrowDown' ? -step : 0);
    else if (k === '+' || k === '=') engine.zoomAt(engine.st.w / 2, engine.st.h / 2, 1.25);
    else if (k === '-' || k === '_') engine.zoomAt(engine.st.w / 2, engine.st.h / 2, 0.8);
    else if (k === '0') engine.fit(state.motion === 'off' ? 0 : 520);
    else if (k >= '1' && k <= '4') goLevel(+k);
    else if ((k === 'Enter' || k === ' ') && f) focusNode(f.id, true);
    else if (k === 'Escape') { if (state.selectedId) select(null); else setFocus(null); }
    else if (k === '/') { $('#lsc-search').focus(); }
    else if (k === 'i') setIso(!state.iso);
    else if (k === 'p') togglePlay();
    else handled = false;
    if (handled) e.preventDefault();
  });

  // ---------- search ----------
  const search = $('#lsc-search'), results = $('#lsc-results');
  function runSearch() {
    const q = search.value.trim().toLowerCase();
    results.replaceChildren();
    if (!q) { results.hidden = true; return []; }
    const hits = state.scene.nodes.filter(n => (n.label + ' ' + (n.sub || '') + ' ' + state.facts.warnings.filter(w => w.node === n.id).map(w => w.title).join(' ') + ' ' + M.dataLocations.filter(d => d.node === n.id).map(d => d.label).join(' ')).toLowerCase().includes(q)).slice(0, 6);
    results.hidden = !hits.length;
    for (const n of hits) results.append(el('li', null, el('button', { type: 'button', onclick: () => { focusNode(n.id, true); results.hidden = true; wrap.focus(); } }, n.label, el('small', { text: ' ' + (n.sub || n.kind) }))));
    return hits;
  }
  search.addEventListener('input', runSearch);
  search.addEventListener('keydown', e => {
    if (e.key === 'Enter') { const h = runSearch(); if (h.length) { focusNode(h[0].id, true); results.hidden = true; wrap.focus(); } e.preventDefault(); }
    if (e.key === 'Escape') { search.value = ''; runSearch(); wrap.focus(); }
  });

  // ---------- replay: recorded deltas, batched to one visual update a second ----------
  const events = M.events, END = events[events.length - 1].t;
  const scrub = $('#lsc-scrub'), clockLabel = $('#lsc-clock'), counters = $('#lsc-counters'), logBox = $('#lsc-log');
  scrub.max = String(END);
  const marks = $('#lsc-marks');
  for (const ev of events) marks.append(el('span', { style: `left: ${(ev.t / END * 100).toFixed(2)}%`, title: describeEvent(ev) }));
  function describeEvent(ev) {
    const o = ev.ops[0];
    if (o.op === 'work') return `${o.card}: ${o.step}, to ${nodeLabel(o.path[o.path.length - 1])}`;
    if (o.op === 'deploy') return `${nodeLabel(o.node)}: ${o.step}`;
    if (o.op === 'link') return `${o.link === 'l-tunnel' ? 'Tunnel' : o.link} ${o.set.state}`;
    if (o.op === 'restart') return `${nodeLabel(o.node)} restarted`;
    if (o.op === 'inside') return `${nodeLabel(o.node)}: ${o.add ? o.add.text : 'done: ' + o.remove}`;
    if (o.op === 'assess') return `Reliability engineer reassessed`;
    if (o.op === 'dispatch-rejected') return `${o.card} refused by ${nodeLabel(o.node)}: ${o.reason}`;
    if (o.op === 'storage') return `Root disk ${o.set.usedPct} % used`;
    if (o.op === 'backup-check') return `Backup overview polled: ${o.state}, unchanged`;
    if (o.op === 'end') return 'End of recording';
    return 'Telemetry update';
  }
  function applyOp(o, animate) {
    const F = state.facts;
    const link = id => state.scene.links.find(l => l.id === id);
    const fx = animate && state.motion !== 'off';
    switch (o.op) {
      case 'patch': Object.assign(F.obs[o.node] = F.obs[o.node] || {}, o.set); break;
      case 'inside': {
        const ins = F.inside[o.node] = F.inside[o.node] || { items: [] };
        ins.items = ins.items || [];
        if (o.add) ins.items.push(o.add);
        if (o.remove) ins.items = ins.items.filter(x => x.text !== o.remove);
        break;
      }
      case 'work': if (fx) engine.addToken({ path: o.path, color: o.kind, label: `${o.card}: ${o.step}`, hopMs: 900 }); break;
      case 'effect': if (fx) engine.addEffect({ kind: o.kind, node: o.node, id: o.id, color: o.color, sustain: true, dur: 1000 }); break;
      case 'effect-end': engine.removeEffect(o.id); break;
      case 'restart': if (fx) engine.addEffect({ kind: 'restart', node: o.node, dur: 1600 }); break;
      case 'link': { const l = link(o.link); Object.assign(l, o.set); if (fx && o.set.state === 'down') { engine.addEffect({ kind: 'snap', node: l.from, dur: 900 }); engine.addEffect({ kind: 'snap', node: l.to, dur: 900 }); } break; }
      case 'warn':
        if (o.add) { F.warnings = F.warnings.filter(w => w.id !== o.add.id); F.warnings.push(o.add); }
        if (o.remove) F.warnings = F.warnings.filter(w => w.id !== o.remove);
        if (o.patch) { const w = F.warnings.find(x => x.id === o.patch); if (w) Object.assign(w, o.set); }
        break;
      case 'storage': { const v = (F.volumes[o.machine] || []).find(x => x.id === o.volume); if (v) Object.assign(v, o.set); break; }
      case 'assess': state.latest[o.persona] = M.specs[o.spec]; break;
      default: break;
    }
  }
  function flush(animate) {
    // One visual update for everything that arrived since the last one.
    if (!state.pending.length) return;
    const batch = state.pending.splice(0);
    let assessed = false;
    for (const ev of batch) {
      for (const o of ev.ops) { applyOp(o, animate); if (o.op === 'assess') assessed = true; }
      if (animate) pushLog(ev);
    }
    state.updates++;
    painter.reindex();
    if (assessed) applyPersona(); else { renderWarnings(); renderDetail(); renderOutline(); renderAssessmentTime(); }
    drawMinimap();
    engine.invalidate();
    renderCounters();
  }
  function renderAssessmentTime() { const prov = host.querySelector('.lsc-prov'); if (prov && state.spec) prov.textContent = prov.textContent.replace(/, [^,·]*ago|, just now/, ', ' + ago(state.spec.createdAt)); }
  function pushLog(ev) {
    const stamp = nowIso().slice(11, 19);
    state.log.unshift(stamp + ' ' + describeEvent(ev));
    state.log = state.log.slice(0, 8);
    logBox.replaceChildren(...state.log.map(s => el('li', { text: s })));
  }
  function renderCounters() {
    counters.textContent = `${state.received} deltas received · ${state.updates} visual updates · at most one a second`;
    clockLabel.textContent = nowIso().slice(11, 19) + ' UTC';
    scrub.value = String(state.clock);
  }
  let last = 0, timer = 0, batchTimer = 0;
  function tick(ts) {
    if (!state.playing) return;
    const dt = last ? (ts - last) / 1000 : 0;
    last = ts;
    state.clock = Math.min(END, state.clock + dt * state.speed);
    while (state.cursor < events.length && events[state.cursor].t <= state.clock) {
      const ev = events[state.cursor++];
      state.received += ev.ops.length;
      state.pending.push(ev);
    }
    clockLabel.textContent = nowIso().slice(11, 19) + ' UTC';
    scrub.value = String(state.clock);
    if (state.clock >= END) { flush(true); setPlaying(false); return; }
    timer = setTimeout(() => requestAnimationFrame(tick), 100);
  }
  function setPlaying(p) {
    state.playing = p;
    const btn = $('#lsc-play');
    btn.textContent = p ? 'Pause' : state.clock >= END ? 'Replay again' : 'Play replay';
    btn.setAttribute('aria-pressed', String(p));
    clearTimeout(timer); clearInterval(batchTimer);
    if (p) {
      if (state.clock >= END) seek(0);
      last = 0; requestAnimationFrame(tick);
      batchTimer = setInterval(() => flush(true), 1000);
    }
  }
  function togglePlay() { setPlaying(!state.playing); }
  function seek(t) {
    // Rebuild from the snapshot and apply every delta up to t without animation.
    const wasSel = state.selectedId;
    resetFacts();
    painter.reindex();
    engine.setScene(state.scene);
    state.latest = {}; state.cursor = 0; state.pending = []; state.received = 0; state.updates = 0; state.log = [];
    engine.st.tokens = []; engine.st.effects = [];
    state.clock = t;
    while (state.cursor < events.length && events[state.cursor].t <= t) { const ev = events[state.cursor++]; state.received += ev.ops.length; state.pending.push(ev); }
    const batch = state.pending.splice(0);
    for (const ev of batch) for (const o of ev.ops) applyOp(o, false);
    if (batch.length) state.updates++;
    logBox.replaceChildren();
    painter.reindex(); state.selectedId = wasSel; applyPersona(); drawMinimap(); renderCounters();
  }
  $('#lsc-play').addEventListener('click', togglePlay);
  scrub.addEventListener('input', () => { setPlaying(false); seek(+scrub.value); });
  for (const b of host.querySelectorAll('[data-control="speed"] button')) b.addEventListener('click', () => { state.speed = +b.dataset.value; pressed('speed', b.dataset.value); });
  for (const b of host.querySelectorAll('[data-control="level"] button')) b.addEventListener('click', () => goLevel(+b.dataset.value));
  $('#lsc-fit').addEventListener('click', () => engine.fit(state.motion === 'off' ? 0 : 520));
  $('#lsc-zoom-in').addEventListener('click', () => engine.zoomAt(engine.st.w / 2, engine.st.h / 2, 1.3));
  $('#lsc-zoom-out').addEventListener('click', () => engine.zoomAt(engine.st.w / 2, engine.st.h / 2, 1 / 1.3));

  // ---------- lifecycle: size, visibility, motion preference ----------
  function size() {
    const r = wrap.getBoundingClientRect();
    const w = Math.round(r.width), h = Math.round(r.height);
    if (!w || !h) return;
    const first = engine.st.w === 300 && engine.st.h === 200;
    engine.resize(w, h, window.devicePixelRatio || 1);
    if (first || !state.fitted) { engine.fit(0); state.fitted = true; }
    drawMinimap(); updateLevel();
  }
  new ResizeObserver(size).observe(wrap);
  document.addEventListener('visibilitychange', () => { engine.setHidden(document.hidden); if (!document.hidden) flush(false); });
  if ('IntersectionObserver' in window) new IntersectionObserver(es => { for (const x of es) engine.setOffscreen(!x.isIntersecting); }).observe(wrap);
  reduce.addEventListener('change', () => setMotion(reduce.matches ? 'off' : 'lively'));

  buildControls();
  readTokens();
  renderLegend();
  applyPersona();
  renderCounters();
  size();

  // Test and capture hooks; not part of the product surface.
  window.LSC = { engine, state, seek, setPlaying, goLevel, toggleLayer, focusNode, setIso, setMotion, flush, select, applyPersona, painter,
    setPersona(id) { state.persona = id; state.specOverride = null; pressed('persona', id); applyPersona(); },
    setLayers(ids) { for (const l of L.LAYERS) toggleLayer(l.id, ids.includes(l.id)); } };
})();
