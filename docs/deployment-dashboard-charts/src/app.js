// Shell of the prototype: filters with their state in the URL, cross-filter,
// persona assessments as validated view specifications, the virtualised card
// list, and the two lazy chunks (2D charts, 3D view). No chart code is imported
// statically: the shell is what the Studio's initial bundle would carry.
import { synth, installation, aggregate, landscape, ENVS, GROUPS, OUTCOME_LABEL, STAGE_LABEL, DAY } from './data.js';
import { readTheme } from './theme.js';
import { validate, factsOf } from './spec.js';
import { PERSONAS, specsFor } from './assess.js';

const $ = (s, r = document) => r.querySelector(s), stage = $('#dd'), el = (tag, cls, text, parent) => { const n = document.createElement(tag); if (cls) n.className = cls; if (text != null) n.textContent = text; if (parent) parent.appendChild(n); return n; };
const KEYS = { group: 'g', project: 'p', env: 'e', tenant: 't', outcome: 'o', stage: 's' };
const state = { group: -1, project: -1, env: -1, tenant: -1, outcome: -1, stage: -1, days: 90, from: null, to: null, persona: 'facts', set: 'large', split: false, view3d: false, theme: 'auto', table: false };
let ds, vm, charts, view3d, specs, spec, problems = [], perf = { first: null, all: null, filter: [] };
const fmt = (v, d) => (v == null ? 'n/a' : v.toLocaleString('en-GB', { maximumFractionDigits: d, minimumFractionDigits: d }));
const day = t => new Date(t * 1000).toISOString().slice(0, 10);

// ---- URL state: every filter, the persona and the 3D switch survive a reload and a pasted link
function readUrl() {
  const q = new URLSearchParams((location.hash || '').slice(1));
  for (const k in KEYS) if (q.has(KEYS[k])) state[k] = +q.get(KEYS[k]);
  if (q.has('d')) state.days = +q.get('d') || 90;
  if (q.has('from')) { state.from = Date.parse(q.get('from')) / 1000; state.to = Date.parse(q.get('to')) / 1000 + DAY; }
  for (const k of ['persona', 'set', 'theme']) if (q.has(k)) state[k] = q.get(k);
  state.split = q.has('split'); state.view3d = q.has('v3d'); state.table = q.has('table');
}
function writeUrl() {
  const q = new URLSearchParams();
  for (const k in KEYS) if (state[k] >= 0) q.set(KEYS[k], state[k]);
  if (state.from) { q.set('from', day(state.from)); q.set('to', day(state.to - DAY)); } else if (state.days !== 90) q.set('d', state.days);
  for (const [k, d] of [['persona', 'facts'], ['set', 'large'], ['theme', 'auto']]) if (state[k] !== d) q.set(k, state[k]);
  if (state.split) q.set('split', 1); if (state.view3d) q.set('v3d', 1); if (state.table) q.set('table', 1);
  const hash = '#' + q.toString();
  try { history.replaceState(null, '', hash); } catch { /* the Dossier viewer's sandbox has no history */ }
  $('#dd-link').value = hash;
}
const filters = () => ({ ...state, from: state.from ?? ds.asOf - state.days * DAY, to: state.to ?? ds.asOf });

// ---- Filter controls
function fillControls() {
  const opt = (sel, items, all) => { sel.replaceChildren(); el('option', '', all, sel).value = -1; items.forEach(([v, t]) => { el('option', '', t, sel).value = v; }); };
  opt($('#f-group'), GROUPS.map((g, i) => [i, g]).filter(([i]) => ds.projects.some(p => p.group === i)), 'All groups');
  opt($('#f-project'), ds.projects.filter(p => state.group < 0 || p.group === state.group).map(p => [p.i, p.name]), 'All projects');
  opt($('#f-env'), ENVS.map((e, i) => [i, e]).filter(([i]) => ds.projects.some(p => p.envs.includes(i))), 'All environments');
  opt($('#f-tenant'), ds.tenants.map((t, i) => [i, t]), 'All tenants');
  opt($('#f-outcome'), OUTCOME_LABEL.slice(0, 4).map((t, i) => [i, t]), 'Any outcome');
  for (const k of ['group', 'project', 'env', 'tenant', 'outcome']) $('#f-' + k).value = state[k];
  $('#f-from').value = day(filters().from); $('#f-to').value = day(filters().to - (state.from ? DAY : 0));
  $('#f-from').min = $('#f-to').min = day(ds.asOf - ds.days * DAY); $('#f-from').max = $('#f-to').max = day(ds.asOf);
  for (const b of stage.querySelectorAll('[data-days]')) b.setAttribute('aria-pressed', String(!state.from && +b.dataset.days === state.days));
  for (const b of stage.querySelectorAll('[data-set]')) b.setAttribute('aria-pressed', String(b.dataset.set === state.set));
  for (const b of stage.querySelectorAll('[data-theme]')) b.setAttribute('aria-pressed', String(b.dataset.theme === state.theme));
  for (const b of stage.querySelectorAll('[data-persona]')) b.setAttribute('aria-pressed', String(b.dataset.persona === state.persona));
  $('#f-split').setAttribute('aria-pressed', String(state.split)); $('#t-table').setAttribute('aria-pressed', String(state.table));
  const chips = $('#dd-chips'); chips.replaceChildren();
  const chip = (text, clear) => { const b = el('button', 'dd-chip', text + ' ×', chips); b.type = 'button'; b.setAttribute('aria-label', 'Remove filter ' + text); b.addEventListener('click', () => { clear(); change(); }); };
  if (state.group >= 0) chip('Group: ' + GROUPS[state.group], () => { state.group = -1; });
  if (state.project >= 0) chip('Project: ' + ds.projects[state.project].name, () => { state.project = -1; });
  if (state.env >= 0) chip('Environment: ' + ENVS[state.env], () => { state.env = -1; });
  if (state.tenant >= 0) chip(ds.tenants[state.tenant], () => { state.tenant = -1; });
  if (state.outcome >= 0) chip('Outcome: ' + OUTCOME_LABEL[state.outcome], () => { state.outcome = -1; });
  if (state.stage >= 0) chip('Cards: ' + STAGE_LABEL[state.stage], () => { state.stage = -1; });
  if (state.from) chip(`${day(state.from)} to ${day(state.to - DAY)}`, () => { state.from = state.to = null; });
  if (chips.children.length) { const c = el('button', 'dd-chip clear', 'Clear all', chips); c.type = 'button'; c.addEventListener('click', () => { Object.assign(state, { group: -1, project: -1, env: -1, tenant: -1, outcome: -1, stage: -1, from: null, to: null }); change(); }); }
}
// A mark clicked in one view filters the others; clicking it again releases the filter.
function onSelect(kind, v) {
  if (kind === 'tile') { const same = state.project === v.p && state.env === v.e; state.project = same ? -1 : v.p; state.env = same ? -1 : v.e; }
  else if (kind === 'project') state.project = state.project === v ? -1 : v;
  else if (kind === 'stage') state.stage = state.stage === v ? -1 : v;
  else if (kind === 'outcome') state.outcome = state.outcome === v ? -1 : v;
  else if (kind === 'range') { if (v) { state.from = Math.floor(v.from / DAY) * DAY; state.to = Math.max(state.from + DAY, Math.ceil(v.to / DAY) * DAY); } else state.from = state.to = null; }
  change();
}

// ---- Tooltip, also shown for the keyboard focus of the tile map
const tipEl = el('div', 'dd-tip', null, stage); tipEl.setAttribute('role', 'status'); tipEl.hidden = true;
function tip(d, x, y) {
  if (!d) { tipEl.hidden = true; return; }
  tipEl.replaceChildren(); el('b', '', d.title, tipEl); const dl = el('dl', '', null, tipEl);
  for (const [k, v] of d.rows) { el('dt', '', k, dl); el('dd', '', v, dl); }
  tipEl.hidden = false; const b = stage.getBoundingClientRect(), w = tipEl.offsetWidth, h = tipEl.offsetHeight;
  tipEl.style.left = Math.max(4, Math.min(b.width - w - 4, x - b.left + 14)) + 'px'; tipEl.style.top = (y - b.top + 18 + h > b.height ? y - b.top - h - 12 : y - b.top + 18) + 'px';
}

// ---- Delivery figures: stat tiles, value first, comparison with the period before
function drawFigures() {
  vm.figures.forEach((f, i) => {
    const t = $('#fig-' + i); $('.l', t).textContent = f.label; $('.v', t).textContent = fmt(f.value, f.digits); $('.u', t).textContent = f.unit;
    const d = $('.d', t), diff = f.value != null && f.before ? f.value - f.before : null; d.className = 'd';
    if (diff == null) d.textContent = 'no period before to compare';
    else { const same = Math.abs(diff) < 0.5 / 10 ** f.digits, good = same ? null : (diff > 0) === (f.good === 'up'); d.textContent = `${same ? '=' : diff > 0 ? '▲' : '▼'} ${fmt(Math.abs(diff), f.digits)} vs the ${Math.round((vm.to - vm.from) / DAY)} days before (${good == null ? 'unchanged' : good ? 'better' : 'worse'})`; if (good != null) d.classList.add(good ? 'good' : 'bad'); }
    t.classList.toggle('strong', !!(spec && (spec.figures || []).includes(f.id)));
    const m = marks().find(x => x.view === 'figures' && x.figure === f.id); $('.n', t).textContent = m ? m.n : ''; $('.n', t).hidden = !m;
  });
}

// ---- Card list: 5,000 cards, a few dozen rows in the DOM
const ROW = 30;
function drawCards() {
  const box = $('#dd-cards'), open = state.stage >= 0; $('#dd-cards-wrap').hidden = !open; if (!open) return;
  const n = vm.cards.length; $('#dd-cards-title').textContent = `${STAGE_LABEL[state.stage]}: ${n.toLocaleString('en-GB')} cards`;
  const inner = $('#dd-cards-inner'); inner.style.height = n * ROW + 'px';
  const first = Math.max(0, Math.floor(box.scrollTop / ROW) - 4), last = Math.min(n, first + Math.ceil(box.clientHeight / ROW) + 8); inner.replaceChildren();
  for (let i = first; i < last; i++) {
    const k = vm.cards[i], c = ds.cards, row = el('div', 'dd-card', null, inner); row.style.top = i * ROW + 'px'; row.setAttribute('role', 'row');
    el('span', 'k', 'CARD-' + c.key[k], row); el('span', 'p', ds.projects[c.p[k]].name, row); el('span', 's', STAGE_LABEL[c.stage[k]], row);
    el('span', 't', 'integrated ' + day(c.tInt[k]), row); el('span', 'w', c.stage[k] < 3 ? Math.max(1, Math.round((ds.asOf - (c.tStg[k] || c.tInt[k])) / DAY)) + ' d waiting' : 'lead ' + Math.round((c.tProd[k] - c.tInt[k]) / 3600) + ' h', row);
  }
  $('#dd-cards-count').textContent = `rows ${first + 1} to ${last} of ${n.toLocaleString('en-GB')} in the DOM`;
}

// ---- Assessments: a validated view specification on top of the facts, or the facts alone
function marks() {
  if (!spec) return [];
  const pi = n => ds.projects.findIndex(p => p.name === n), strong = t => (spec.emphasis || []).some(e => e.weight === 'strong' && JSON.stringify(e.target) === JSON.stringify(t));
  const out = (spec.annotations || []).map((a, i) => { const t = a.target; return { n: i + 1, view: t.view, p: pi(t.project), e: ENVS.indexOf(t.env), node: t.node, figure: t.figure, from: Date.parse(t.from) / 1000, to: Date.parse(t.to) / 1000 + DAY, strong: strong(t) }; });
  return out;
}
function drawAssessment() {
  const raw = state.persona === 'facts' ? null : specs[state.persona]; spec = null; problems = [];
  if (raw) { const r = validate(raw, factsOf(ds)); if (r.ok) spec = raw; else problems = r.errors; }
  const box = $('#dd-assess'); box.replaceChildren(); box.dataset.state = spec ? 'ok' : raw ? 'rejected' : 'facts';
  if (!raw) el('p', 'dd-note', 'Facts only: every mark below comes from the deploy records, the structure facts and the backup overview. Pick a persona to see its assessment on top.', box);
  else if (!spec) {
    el('h3', '', 'Assessment rejected, showing facts only', box); el('p', 'dd-note', 'The specification did not pass validation, so nothing of it is drawn. Reasons:', box);
    const ul = el('ul', '', null, box); problems.slice(0, 6).forEach(p => el('li', '', p, ul));
  } else {
    el('h3', '', spec.headline, box); if (spec.judgement) el('p', '', spec.judgement, box);
    const age = Math.round((ds.asOf - Date.parse(spec.createdAt) / 1000) / 60);
    el('p', 'dd-prov', `${spec.persona.title} · assessed ${spec.createdAt.slice(0, 16).replace('T', ' ')} UTC, ${age} minutes before these facts · revision ${spec.revision} · model ${spec.model.id} (${spec.model.capabilityClass}) · confidence ${Math.round(spec.confidence * 100)} % · ${spec.tokens ? spec.tokens.input + ' in, ' + spec.tokens.output + ' out tokens' : ''}`, box);
    const fu = el('details', '', null, box); el('summary', '', `Facts used (${spec.factsUsed.length})`, fu); const ful = el('ul', '', null, fu); spec.factsUsed.forEach(f => el('li', '', f, ful));
    const ol = el('ol', 'dd-callouts', null, box);
    (spec.annotations || []).forEach(a => { const li = el('li', 'tone-' + a.tone, null, ol); el('b', '', a.title, li); el('span', '', ' ' + a.text, li); if (a.blocks) el('span', 'x', ' Blocks: ' + a.blocks, li); if (a.next) el('span', 'x', ' Next: ' + a.next, li); el('span', 'where', ` (${a.target.view}${a.target.project ? ': ' + a.target.project + ' · ' + a.target.env : a.target.node ? ': ' + a.target.node : a.target.figure ? ': ' + a.target.figure : ''})`, li); });
    for (const goal of spec.goals || []) {
      const gEl = el('div', 'dd-goal', null, box); el('b', '', `Goal: ${goal.title}`, gEl); el('span', 'count', ` ${goal.done} of ${goal.total}`, gEl);
      const bar = el('div', 'bar', null, gEl); bar.setAttribute('role', 'img'); bar.setAttribute('aria-label', `${goal.done} of ${goal.total} done`); for (let i = 0; i < goal.total; i++) el('i', i < goal.done ? 'done' : '', null, bar);
      const steps = el('ol', '', null, gEl); goal.steps.forEach(s => { const li = el('li', 'st-' + s.state, null, steps); el('b', '', `${s.kind === 'owner-decision' ? 'Owner decision' : s.kind === 'card' ? 'Card' : 'Operation'}: ${s.title}`, li); el('span', '', ` ${s.detail || ''} (${s.state})`, li); });
    }
    const det = el('details', '', null, box); el('summary', '', 'Raw specification', det); el('pre', '', JSON.stringify(spec, null, 1), det);
  }
  // The persona may reorder the views; without one the order is the default.
  const order = (spec && spec.order && spec.order.views) || ['figures', 'overview', 'flow', 'timeline'];
  for (const s of stage.querySelectorAll('[data-view]')) s.style.order = String(10 + (order.indexOf(s.dataset.view) < 0 ? 9 : order.indexOf(s.dataset.view)));
}
function drawTable() { // text alternative of the tile map, built only when asked for
  const host = $('#dd-table'); host.hidden = !state.table; if (!state.table) { host.replaceChildren(); return; }
  const t = el('table'), head = el('tr', '', null, el('thead', '', null, t)); el('th', '', 'Project', head); vm.envs.forEach(e => el('th', '', e.name, head)); const body = el('tbody', '', null, t);
  for (const p of vm.projects) { const tr = el('tr', '', null, body); el('th', '', p.name, tr); for (const e of vm.envs) { const c = vm.tiles.find(x => x.p === p.i && x.e === e.i); el('td', '', !c.setUp ? 'not set up' : `r${c.head - c.drift}, ${c.drift} behind, last run ${(OUTCOME_LABEL[c.lastOutcome] || 'none').toLowerCase()}, ${['up', 'degraded', 'down', 'unknown'][c.health]}${c.approvals ? ', ' + c.approvals + ' approvals waiting' : ''}${c.noBackup ? ', no backup' : ''}`, tr); } }
  host.replaceChildren(t);
}

// ---- Render cycle
function render(measure) {
  const t0 = performance.now(); vm = aggregate(ds, filters());
  fillControls(); drawAssessment(); drawFigures(); drawCards(); drawTable();
  if (charts) { charts.update(vm); charts.annotate(marks()); }
  if (view3d) view3d.update(landscape(ds, filters()));
  $('#dd-count').textContent = `${vm.projects.length} of ${ds.projects.length} projects · ${vm.runs.length.toLocaleString('en-GB')} of ${ds.runs.n.toLocaleString('en-GB')} deploy runs · ${(vm.flow.nodes[0].count).toLocaleString('en-GB')} of ${ds.cards.n.toLocaleString('en-GB')} cards · as of ${day(ds.asOf)} 20:00 UTC`;
  writeUrl();
  if (measure) requestAnimationFrame(() => { perf.filter.push(performance.now() - t0); showPerf(); });
}
const change = () => render(true);
function showPerf() {
  const f = perf.filter.slice(-20).sort((a, b) => a - b), med = f.length ? f[f.length >> 1] : null;
  $('#dd-perf').textContent = `Data to first chart ${fmt(perf.first, 0)} ms, to all charts ${fmt(perf.all, 0)} ms · last filter change ${fmt(perf.filter.at(-1), 0)} ms (median ${fmt(med, 0)} ms of ${f.length}) · ${stage.querySelectorAll('*').length} DOM elements in the view · motion ${stage.dataset.motion}`;
  window.__dd = { perf, state, vm: () => vm, frames3d: () => (view3d ? view3d.frames() : 0) };
}
const ctx = () => ({ ds, theme: readTheme(stage), animate: stage.dataset.motion === 'on', onSelect, tip, split: () => state.split, filters: () => state, groups: GROUPS, brush: () => aggregate(ds, { ...filters(), outcome: -1, from: null, to: null }).day, readout: t => { $('#dd-3d-readout').textContent = t || 'Drag to turn, arrow keys work too. Hover a bar for its figures.'; } });

async function load() {
  if (charts) { charts.dispose(); charts = null; }
  ds = state.set === 'large' ? synth() : installation(); specs = specsFor(ds);
  for (const k of ['project', 'tenant']) if (state[k] >= (k === 'project' ? ds.projects.length : ds.tenants.length)) state[k] = -1;
  // Motion is off above the data threshold and with reduced motion.
  stage.dataset.motion = ds.runs.n > 1000 || matchMedia('(prefers-reduced-motion: reduce)').matches ? 'off' : 'on';
  const t0 = performance.now(); render(false); perf.shell = performance.now() - t0;
  const { mount } = await import('./charts.js'); // lazy chunk 1: the 2D charts
  charts = mount({ overview: $('#v-overview'), flow: $('#v-flow'), timeline: $('#v-timeline'), brush: $('#v-brush'), figures: [0, 1, 2, 3].map(i => $('#fig-' + i + ' .s')) }, vm, ctx());
  charts.annotate(marks()); perf.first = performance.now() - t0;
  requestAnimationFrame(() => requestAnimationFrame(() => { perf.all = performance.now() - t0; showPerf(); }));
  if (state.view3d) open3d();
}
async function open3d() {
  state.view3d = true; $('#dd-3d').hidden = false; $('#dd-3d-open').hidden = true; writeUrl();
  if (view3d) { view3d.dispose(); view3d = null; }
  const { mount3d } = await import('./view3d.js'); // lazy chunk 2: only when the 3D view is opened
  view3d = mount3d($('#v-3d'), landscape(ds, filters()), ctx()); ctx().readout('');
}
function applyTheme() {
  const dark = state.theme === 'auto' ? matchMedia('(prefers-color-scheme: dark)').matches : state.theme === 'dark';
  stage.dataset.studioTheme = dark ? 'dark' : 'light'; const t = readTheme(stage); if (charts) charts.retheme(t); if (view3d) view3d.retheme(t);
}

// ---- Wiring
for (const k of ['group', 'project', 'env', 'tenant', 'outcome']) $('#f-' + k).addEventListener('change', ev => { state[k] = +ev.target.value; if (k === 'group') state.project = -1; change(); });
for (const id of ['from', 'to']) $('#f-' + id).addEventListener('change', () => { const a = Date.parse($('#f-from').value) / 1000, b = Date.parse($('#f-to').value) / 1000 + DAY; if (a < b) { state.from = a; state.to = b; change(); } });
stage.addEventListener('click', ev => {
  const b = ev.target.closest('button'); if (!b) return;
  if (b.dataset.days) { state.days = +b.dataset.days; state.from = state.to = null; change(); }
  else if (b.dataset.persona) { state.persona = b.dataset.persona; change(); }
  else if (b.dataset.set) { state.set = b.dataset.set; Object.assign(state, { group: -1, project: -1, env: -1, tenant: -1, stage: -1 }); load(); }
  else if (b.dataset.theme) { state.theme = b.dataset.theme; applyTheme(); fillControls(); writeUrl(); }
  else if (b.id === 'f-split') { state.split = !state.split; change(); if (charts) charts.retheme(readTheme(stage)); }
  else if (b.id === 't-table') { state.table = !state.table; change(); }
  else if (b.id === 'dd-3d-open') open3d();
  else if (b.id === 'dd-3d-close') { state.view3d = false; if (view3d) view3d.dispose(); view3d = null; $('#dd-3d').hidden = true; $('#dd-3d-open').hidden = false; writeUrl(); }
  else if (b.dataset.turn) view3d && view3d.rotate(+b.dataset.turn);
  else if (b.id === 'dd-3d-flat') { const on = b.getAttribute('aria-pressed') !== 'true'; b.setAttribute('aria-pressed', String(on)); view3d && view3d.flatten(on); }
  else if (b.dataset.outcome) onSelect('outcome', +b.dataset.outcome);
});
$('#dd-cards').addEventListener('scroll', () => requestAnimationFrame(drawCards));
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', applyTheme);
addEventListener('hashchange', () => { readUrl(); render(false); });
readUrl(); applyTheme(); load();
