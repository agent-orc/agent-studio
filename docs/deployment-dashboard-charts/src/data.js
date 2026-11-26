// Facts for the deployment dashboard prototype: a seeded synthetic set of the
// size the budget names (40 projects, 4 environments, 6 tenants, 2,000 deploy
// runs, 5,000 cards) and aggregate(), the one function every view reads. In the
// product aggregate() is the server side of GET /api/deploy/dashboard.
export const ENVS = ['Preview', 'Staging', 'Canary', 'Production'];
export const OUTCOMES = ['ok', 'failed', 'rolled-back', 'cancelled'];
export const OUTCOME_LABEL = ['Succeeded', 'Failed', 'Rolled back', 'Cancelled', 'Running'];
export const STEPS = ['preflight', 'build image', 'push image', 'migrate data', 'switch release', 'smoke test', 'health gate'];
export const STAGES = ['integrated', 'staging', 'approval', 'production'];
export const STAGE_LABEL = ['Integrated, not deployed', 'On Staging', 'Waiting for approval', 'Live in Production'];
export const HEALTH = ['up', 'degraded', 'down', 'unknown'];
export const GROUPS = ['Platform', 'Web', 'Services', 'Data', 'Tools', 'Templates'];
export const DAY = 86400;
export const NOW = Date.UTC(2026, 10, 17, 20, 0, 0) / 1000; // as-of of every figure, seconds

const NAMES = ('studio gateway identity ledger notifier scheduler|storefront landing docs-site console status-page help-centre newsletter|billing search catalogue checkout pricing inventory shipping reviews|'
  + 'warehouse pipelines reports exports metrics archive|cli sdk runner-image linter migrator fixtures|static-web container-service worker-service cron-service api-service edge-function batch-service').split('|').map(s => s.split(' '));

function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }

export function synth(seed = 7, size = {}) {
  const { projects: P = 40, tenants: T = 6, runs: R = 2000, cards: C = 5000, days = 180 } = size;
  const r = rng(seed), E = ENVS.length;
  const pick = w => { let x = r() * w.reduce((a, b) => a + b, 0); for (let i = 0; i < w.length; i++) if ((x -= w[i]) < 0) return i; return w.length - 1; };
  const logn = (median, s) => median * Math.exp(s * (Math.sqrt(-2 * Math.log(1 - r())) * Math.cos(6.2832 * r())));
  const projects = [];
  if (size.list) size.list.forEach((p, i) => projects.push({ i, weight: 1 / (1 + i * 0.5), head: 30 + Math.floor(r() * 380), bad: false, multi: false, approval: true, ...p }));
  for (let g = 0; projects.length < P; g = (g + 1) % GROUPS.length) {
    const n = projects.filter(p => p.group === g).length;
    if (n >= NAMES[g].length) continue;
    const i = projects.length, bare = i % 13 === 11, envs = bare ? [] : [0, 1, 2, 3].filter(e => e === 1 || (e === 3 ? i % 9 !== 4 : e === 2 ? i % 3 === 0 : i % 4 !== 3));
    projects.push({ i, name: NAMES[g][n], group: g, envs, multi: envs.includes(3) && i % 2 === 0, approval: i % 3 !== 1, weight: 1 / (1 + i * 0.12) + r() * 0.2, head: 20 + Math.floor(r() * 400), bad: i === 9 || i === 20 });
  }
  projects.sort((a, b) => a.group - b.group || a.i - b.i).forEach((p, i) => { p.i = i; });
  const w = projects.map(p => p.weight);
  // Deploy runs.
  const runs = { n: R, p: new Uint8Array(R), e: new Uint8Array(R), t: new Int8Array(R), start: new Float64Array(R), dur: new Float32Array(R), outcome: new Uint8Array(R), step: new Int8Array(R), rel: new Uint16Array(R), restore: new Float32Array(R) };
  const starts = [];
  for (let k = 0; k < R; k++) { const d = Math.floor(Math.pow(r(), 0.8) * days), wd = new Date((NOW - d * DAY) * 1000).getUTCDay(); if ((wd === 0 || wd === 6) && r() < 0.8) { k--; continue; } starts.push(NOW - d * DAY - DAY + (7 + r() * 11) * 3600); }
  starts.sort((a, b) => a - b);
  for (let k = 0; k < R; k++) {
    let p; do { p = projects[pick(w)]; } while (!p.envs.length);
    const e = p.envs[pick(p.envs.map(x => [35, 35, 10, 20][x]))], age = (NOW - starts[k]) / DAY;
    const badWeek = p.bad && age > 38 && age < 52;
    const o = k > R - 4 && e !== 3 ? 4 : pick(badWeek ? [45, 40, 12, 3] : [88, 7, 3, 2]);
    runs.p[k] = p.i; runs.e[k] = e; runs.t[k] = p.multi && e >= 2 ? Math.floor(r() * T) : -1; runs.start[k] = starts[k]; runs.outcome[k] = o;
    runs.step[k] = o === 1 || o === 2 ? (badWeek ? 3 : pick([8, 20, 6, 18, 12, 22, 14])) : -1;
    runs.dur[k] = Math.min(3600, logn([110, 230, 300, 420][e], 0.45) * (o === 1 ? 0.3 + runs.step[k] * 0.15 : o === 2 ? 1.6 : 1));
    runs.rel[k] = Math.max(1, p.head - Math.floor(age / 3));
  }
  for (let k = 0; k < R; k++) { // time to restore: until the next good release is live again
    const o = runs.outcome[k], age = (NOW - runs.start[k]) / DAY;
    runs.restore[k] = o === 1 || o === 2 ? logn(projects[runs.p[k]].bad && age > 38 && age < 52 ? 170 * 60 : 38 * 60, 0.7) : -1;
  }
  // Cards: integration time, then staging and production times where the project has the environment.
  const cards = { n: C, p: new Uint8Array(C), tInt: new Float64Array(C), tStg: new Float64Array(C), tProd: new Float64Array(C), stage: new Uint8Array(C), key: new Uint16Array(C) };
  const ints = Array.from({ length: C }, () => NOW - Math.pow(r(), 1.15) * days * DAY).sort((a, b) => a - b);
  for (let k = 0; k < C; k++) {
    const p = projects[pick(w)], tInt = ints[k];
    // A project without Staging deploys straight to Production (the Studio's own instance does).
    const hasStg = p.envs.includes(1), tStg = hasStg ? tInt + logn(5 * 3600, 0.9) : Infinity, tProd = p.envs.includes(3) ? (hasStg ? tStg : tInt) + logn((p.approval ? 60 : 20) * 3600, 0.8) : Infinity;
    cards.p[k] = p.i; cards.tInt[k] = tInt; cards.tStg[k] = tStg < NOW ? tStg : 0; cards.tProd[k] = tProd < NOW ? tProd : 0; cards.key[k] = 1000 + k;
    cards.stage[k] = tProd < NOW ? 3 : tStg < NOW ? (p.approval && p.envs.includes(3) && tProd - NOW < 30 * 3600 ? 2 : 1) : 0;
  }
  // Current state of every project and environment (and tenant).
  const cells = [];
  for (const p of projects) for (let e = 0; e < E; e++) {
    if (!p.envs.includes(e)) { cells.push({ p: p.i, e, setUp: false }); continue; }
    let last = -1; for (let k = R - 1; k >= 0; k--) if (runs.p[k] === p.i && runs.e[k] === e) { last = k; break; }
    const lag = e === 0 ? 0 : e === 1 ? pick([70, 20, 10]) : e === 2 ? pick([40, 25, 20, 10, 5]) : Math.min(14, Math.floor(logn(1.6, 0.9)));
    const o = last < 0 ? -1 : runs.outcome[last], health = o === 1 ? pick([40, 45, 10, 5]) : pick([93, 4, 1, 2]);
    const tenants = p.multi && e >= 2 ? Array.from({ length: T }, () => ({ drift: Math.max(0, lag + pick([60, 20, 10, 10]) - 1), health: pick([92, 5, 1, 2]) })) : null;
    cells.push({ p: p.i, e, setUp: true, drift: tenants ? Math.max(...tenants.map(t => t.drift)) : lag, head: p.head, last, lastOutcome: o, health, approvals: 0,
      ageDays: last < 0 ? null : (NOW - runs.start[last]) / DAY, tenants, noBackup: e === 3 && (size.list ? true : p.i % 4 !== 0), warn: (p.bad && e >= 1) || (p.i * 7 + e) % 23 === 0 ? 1 : 0 });
  }
  for (let k = 0; k < C; k++) if (cards.stage[k] === 2) cells[cards.p[k] * E + 3].approvals++;
  return { asOf: NOW, days, projects, envs: ENVS, tenants: Array.from({ length: T }, (_, i) => 'Tenant ' + (i + 1)), runs, cards, cells, synthetic: !size.list };
}

const median = a => { if (!a.length) return null; a.sort((x, y) => x - y); const m = a.length >> 1; return a.length % 2 ? a[m] : (a[m - 1] + a[m]) / 2; };

// Filters: { group, project, env, tenant, outcome: index or -1; from, to: seconds; stage: -1 or stage index }
export function aggregate(ds, f) {
  const E = ds.envs.length, { runs, cards } = ds, from = f.from ?? ds.asOf - ds.days * DAY, to = f.to ?? ds.asOf;
  const pOk = new Uint8Array(ds.projects.length);
  const projects = ds.projects.filter(p => (f.group < 0 || p.group === f.group) && (f.project < 0 || p.i === f.project));
  for (const p of projects) pOk[p.i] = 1;
  // Only environments that at least one project of the filter has; a project's own gaps stay visible as not set up.
  const used = ds.envs.map((name, i) => ({ i, name })).filter(e => projects.some(p => p.envs.includes(e.i)));
  const envs = (used.length ? used : ds.envs.map((name, i) => ({ i, name }))).filter(e => f.env < 0 || e.i === f.env);
  const direct = projects.some(p => p.envs.includes(3) && !p.envs.includes(1));
  // Overview: state now, one tile per project and environment; filters dim, they do not remove.
  const tiles = [];
  for (const p of projects) for (const e of envs) {
    const c = ds.cells[p.i * E + e.i], t = c.setUp && c.tenants && f.tenant >= 0 ? c.tenants[f.tenant] : null;
    tiles.push({ ...c, drift: t ? t.drift : c.drift, health: t ? t.health : c.health, match: c.setUp && (f.outcome < 0 || c.lastOutcome === f.outcome) && (f.tenant < 0 || !!c.tenants || c.e < 2) });
  }
  // Timeline: runs in the filter, as columns for a time chart, plus counts a day by outcome.
  const idx = [], days = Math.max(1, Math.ceil((to - from) / DAY)), day = [0, 1, 2, 3].map(() => new Float64Array(days));
  for (let k = 0; k < runs.n; k++) {
    if (!pOk[runs.p[k]] || runs.start[k] < from || runs.start[k] > to) continue;
    if ((f.env >= 0 && runs.e[k] !== f.env) || (f.tenant >= 0 && runs.t[k] >= 0 && runs.t[k] !== f.tenant)) continue;
    if (runs.outcome[k] < 4) day[runs.outcome[k]][Math.min(days - 1, Math.floor((runs.start[k] - from) / DAY))]++;
    if (f.outcome < 0 || runs.outcome[k] === f.outcome) idx.push(k);
  }
  const dayX = Float64Array.from({ length: days }, (_, d) => from + d * DAY);
  // Release flow: where the cards of the filter are now.
  const stage = [0, 0, 0, 0], list = [], byProject = new Map();
  for (let k = 0; k < cards.n; k++) {
    if (!pOk[cards.p[k]] || cards.tInt[k] < from || cards.tInt[k] > to) continue;
    const s = cards.stage[k]; stage[s]++;
    if (s < 3) { const b = byProject.get(cards.p[k]) || [0, 0, 0]; b[s]++; byProject.set(cards.p[k], b); }
    if (f.stage < 0 || s === f.stage) list.push(k);
  }
  const all = stage[0] + stage[1] + stage[2] + stage[3], onStg = stage[1] + stage[2] + stage[3];
  const flow = {
    nodes: [{ id: 'integrated', label: 'Integrated', col: 0, count: all }, { id: 'wait-staging', label: 'Not deployed yet', col: 1, count: stage[0], stage: 0, stuck: true }, { id: 'staging', label: direct ? 'Deployed: Staging or direct' : 'Reached Staging', col: 1, count: onStg },
      { id: 'held', label: 'On Staging only', col: 2, count: stage[1], stage: 1, stuck: true }, { id: 'approval', label: 'Waiting for approval', col: 2, count: stage[2], stage: 2, stuck: true }, { id: 'production', label: 'Live in Production', col: 2, count: stage[3], stage: 3 }],
    links: [['integrated', 'wait-staging', stage[0]], ['integrated', 'staging', onStg], ['staging', 'held', stage[1]], ['staging', 'approval', stage[2]], ['staging', 'production', stage[3]]].map(([s, t, count]) => ({ s, t, count })),
    waiting: [...byProject].map(([p, b]) => ({ p, name: ds.projects[p].name, counts: b, total: b[0] + b[1] + b[2] })).sort((a, b) => b.total - a.total),
  };
  return { asOf: ds.asOf, from, to, projects, envs, tiles, runs: idx, day: { x: dayX, counts: day }, flow, cards: list, figures: figures(ds, pOk, f, from, to), total: { runs: runs.n, cards: cards.n } };
}

// Delivery figures for Production, by week, with the period before for comparison.
function figures(ds, pOk, f, from, to) {
  const { runs, cards } = ds, span = to - from, weeks = Math.max(2, Math.min(26, Math.round(span / (7 * DAY)))), wLen = span / weeks;
  const stat = (lo, hi) => {
    let deploys = 0, bad = 0, done = 0; const lead = [], restore = [];
    for (let k = 0; k < runs.n; k++) {
      if (runs.e[k] !== 3 || !pOk[runs.p[k]] || runs.start[k] < lo || runs.start[k] >= hi || (f.tenant >= 0 && runs.t[k] >= 0 && runs.t[k] !== f.tenant)) continue;
      const o = runs.outcome[k]; if (o > 2) continue; done++;
      if (o === 0) deploys++; else { bad++; if (runs.restore[k] > 0) restore.push(runs.restore[k] / 60); }
    }
    for (let k = 0; k < cards.n; k++) if (pOk[cards.p[k]] && cards.tProd[k] >= lo && cards.tProd[k] < hi) lead.push((cards.tProd[k] - cards.tInt[k]) / 3600);
    return [deploys / ((hi - lo) / (7 * DAY)), median(lead), done ? 100 * bad / done : null, median(restore)];
  };
  const now = stat(from, to), before = stat(from - span, from), series = [[], [], [], []];
  for (let w = 0; w < weeks; w++) stat(from + w * wLen, from + (w + 1) * wLen).forEach((v, i) => series[i].push(v));
  const meta = [['frequency', 'Deploy frequency', 'deploys a week', 'up', 1], ['lead', 'Lead time, integration to Production', 'hours, median', 'down', 0], ['cfr', 'Change failure rate', '% of Production deploys', 'down', 1], ['mttr', 'Time to restore', 'minutes, median', 'down', 0]];
  return meta.map(([id, label, unit, good, digits], i) => ({ id, label, unit, good, digits, value: now[i], before: before[i], series: series[i], weeks }));
}

// The 3D view: deploy runs by week and project. Height is the count, colour the share that failed.
export function landscape(ds, f, weeks = 26) {
  const P = ds.projects.length, { runs } = ds, count = new Float32Array(weeks * P), fail = new Float32Array(weeks * P), t0 = ds.asOf - weeks * 7 * DAY;
  for (let k = 0; k < runs.n; k++) {
    const p = ds.projects[runs.p[k]];
    if (runs.start[k] < t0 || (f.group >= 0 && p.group !== f.group) || (f.env >= 0 && runs.e[k] !== f.env)) continue;
    const i = Math.min(weeks - 1, Math.floor((runs.start[k] - t0) / (7 * DAY))) * P + runs.p[k]; count[i]++;
    if (runs.outcome[k] === 1 || runs.outcome[k] === 2) fail[i]++;
  }
  let max = 1; for (let i = 0; i < count.length; i++) { if (count[i] > max) max = count[i]; if (count[i]) fail[i] /= count[i]; }
  return { weeks, t0, projects: ds.projects.map(p => p.name), groups: ds.projects.map(p => p.group), count, fail, max };
}

// The installation as the card describes it on 17 November 2026, by role: the Studio itself
// (Production only, instance deploy records), the website (Staging and Production) and projects
// without environments. Counts are of the installation's order of magnitude, not read from its API.
export function installation() {
  const list = [['studio (this instance)', 0, [3]], ['website', 1, [1, 3]], ['documentation site', 1, [1, 3]], ['runner image', 4, [1]], ['static-web template', 5, []], ['container-service template', 5, []], ['companion app (reference)', 4, []]]
    .map(([name, group, envs]) => ({ name, group, envs }));
  return synth(11, { projects: list.length, tenants: 1, runs: 150, cards: 420, days: 180, list });
}
