// Assessments for the prototype: what the two catalogue personas would return
// for the synthetic facts of data.js. In the product these come from a persona
// occurrence (an LLM call) and from the cache; here they are computed from the
// same facts so every mark they name exists, and one rejected example shows
// the fallback. Persona ids, cadence and triggers are those of the catalogue
// in backend/Policies/personas/; no role is added.
import { aggregate, ENVS, DAY } from './data.js';

export const PERSONAS = [
  { id: 'operations-warden', role: 'operations', title: 'Operations warden', cadence: 'P1D, due 06:30', capabilityClass: 'balanced', thinking: 'high', tokensPerWeek: 140000,
    triggers: ['backup.missed', 'backup.failed', 'restore-test.due', 'credential.expiring', 'credential.expired', 'deploy.completed', 'deploy.rolled-back', 'audit.chain-broken'] },
  { id: 'reliability-engineer', role: 'reliability', title: 'Reliability engineer', cadence: 'P1D, due 07:00', capabilityClass: 'balanced', thinking: 'medium', tokensPerWeek: 140000,
    triggers: ['batch.red', 'batch.isolating', 'escalation.opened', 'backend.stalled', 'deploy.completed', 'deploy.rolled-back'] },
];
const MODEL = { capabilityClass: 'balanced', id: 'claude-sonnet-5-5' };
const iso = s => new Date(s * 1000).toISOString();
const dayOf = s => iso(s).slice(0, 10);
const tile = (project, env = 'Production') => ({ view: 'overview', project, env });

export function specsFor(ds) {
  const all = aggregate(ds, { group: -1, project: -1, env: -1, tenant: -1, outcome: -1, stage: -1 });
  const name = p => ds.projects[p].name, prod = ENVS.indexOf('Production');
  const live = ds.cells.filter(c => c.setUp);
  const bare = live.filter(c => c.e === prod && c.noBackup);
  const prodTiles = live.filter(c => c.e === prod).length;
  const worstBare = [...bare].sort((a, b) => b.drift - a.drift)[0] || bare[0];
  const firstBare = bare.find(c => c !== worstBare) || worstBare;
  const site = live.find(c => c.warn && !ds.projects[c.p].bad && c.e >= 1) || live.find(c => c.warn) || live[0];
  const drift = [...live].sort((a, b) => b.drift - a.drift)[0];
  const node = id => all.flow.nodes.find(n => n.id === id).count;
  const waiting = all.flow.waiting.slice(0, 3);
  // The failure cluster: failed and rolled-back runs of the projects flagged bad, 38 to 52 days back.
  const bad = ds.projects.filter(p => p.bad);
  let lo = Infinity, hi = 0, failed = 0, total = 0;
  for (let k = 0; k < ds.runs.n; k++) {
    const age = (ds.asOf - ds.runs.start[k]) / DAY, o = ds.runs.outcome[k];
    if (age <= 38 || age >= 52 || !ds.projects[ds.runs.p[k]].bad) continue;
    total++;
    if (o === 1 || o === 2) { failed++; lo = Math.min(lo, ds.runs.start[k]); hi = Math.max(hi, ds.runs.start[k]); }
  }
  if (!failed) { lo = ds.asOf - 52 * DAY; hi = ds.asOf - 38 * DAY; }
  const badNames = bad.map(p => p.name).join(' and ') || 'two projects';
  const badTile = live.find(c => ds.projects[c.p].bad && c.e === prod) || live.find(c => ds.projects[c.p].bad) || drift;
  const cfr = all.figures.find(f => f.id === 'cfr').value;
  const created = ds.asOf - 20 * 60;

  const warden = {
    schemaVersion: 'deploy-view-spec/v1', id: 'operations-warden-deploy', revision: 4,
    persona: { id: 'operations-warden', role: 'operations', title: 'Operations warden' },
    createdAt: iso(created), model: MODEL, confidence: 0.86, tokens: { input: 5600, output: 720 }, trigger: 'backup.missed, coalesced with the 06:30 occurrence',
    factsUsed: [
      'GET /api/operator/backups: state not-configured, no backup key, off-host target not-configured',
      'GET /api/v1/operations/structure, dataLocations: 1 of 6 must-keep locations has an off-host copy (workspace repository, git remote)',
      'Application state directory: one manual local snapshot of 2026-11-09 on the same disk',
      `GET /api/deploy/dashboard: ${bare.length} of ${prodTiles} Production tiles keep data without a verified off-host copy`,
      'GET /api/v1/management/credential-inventory: one deploy credential expires in 9 days',
      `GET /api/deploy/dashboard, flow: ${node('approval')} cards wait for a Production approval`,
    ],
    headline: `Production runs without a restorable backup: ${bare.length} of ${prodTiles} Production tiles have no verified off-host copy.`,
    judgement: 'Deploys are flowing, so the risk is not delivery but loss. The backup feature exists and reports not-configured: there is no backup key and no off-host target. '
      + 'The only copy of the application state is a manual snapshot of 9 November on the same disk, which a disk failure takes with it; the workspace repository is safe on its git remote. '
      + 'Two owner decisions unblock everything else: the off-host target and the custody of the two keys. Until then every Production deploy adds state that cannot be restored. '
      + 'One deploy credential expires in 9 days and will stop Staging deploys of the project it belongs to.',
    order: { views: ['overview', 'flow', 'figures', 'timeline'] },
    figures: ['frequency'],
    emphasis: [{ target: tile(name(firstBare.p)), weight: 'strong' }, { target: tile(name(worstBare.p)), weight: 'strong' }, { target: tile(name(site.p), ENVS[site.e]), weight: 'normal' }],
    annotations: [
      { id: 'no-backup', target: tile(name(firstBare.p)), tone: 'bad', title: 'No backup here', text: 'State of this Production environment has no verified off-host copy; the same holds for every tile with the backup mark.',
        blocks: 'A restore after a disk loss', next: 'Decide the off-host target, then configure the backup and verify one restore.', factRefs: [0, 1, 3] },
      { id: 'no-backup-drift', target: tile(name(worstBare.p)), tone: 'warn', title: 'Behind and unprotected', text: `${worstBare.drift} release${worstBare.drift === 1 ? "" : "s"} behind and no off-host copy: the next deploy here migrates data that cannot be rolled back from a backup.`,
        next: 'Take a verified backup before the next Production deploy of this project.', factRefs: [3] },
      { id: 'credential', target: tile(name(site.p), ENVS[site.e]), tone: 'warn', title: 'Deploy credential expires in 9 days', text: 'The credential this environment deploys with expires on 26 November.',
        blocks: `Deploys of ${name(site.p)} to ${ENVS[site.e]}`, next: 'Rotate the credential in the credential inventory; the warden reminds 3 days before.', factRefs: [4] },
    ],
    goals: [{ id: 'backup', title: 'Every must-keep location has a verified off-host copy', done: 1, total: 6, steps: [
      { kind: 'owner-decision', title: 'Choose the off-host backup target', detail: 'Decision D7 of docs/live-structure-canvas: a second site the owner controls is recommended.', state: 'open' },
      { kind: 'owner-decision', title: 'Decide the custody of the backup key and the secret store key', detail: 'Decision D8 of docs/live-structure-canvas: two separate entries, attested by fingerprint.', state: 'open' },
      { kind: 'card', title: 'Configure the backup and verify one restore', detail: 'Create the backup key, set the target, run a backup, restore it on a scratch host and record the receipt.', state: 'proposed' },
      { kind: 'operation', title: 'Attest the escrow of both keys', detail: 'Shown as "attested on" next to each key once the owner confirms the fingerprint.', state: 'open' },
    ] }],
  };

  const reliability = {
    schemaVersion: 'deploy-view-spec/v1', id: 'reliability-engineer-deploy', revision: 7,
    persona: { id: 'reliability-engineer', role: 'reliability', title: 'Reliability engineer' },
    createdAt: iso(created + 6 * 60), model: MODEL, confidence: 0.78, tokens: { input: 5400, output: 690 }, trigger: 'deploy.rolled-back, coalesced with the 07:00 occurrence',
    factsUsed: [
      `GET /api/deploy/dashboard, runs: ${failed} of ${total} runs of ${badNames} failed or rolled back between ${dayOf(lo)} and ${dayOf(hi)}, most at step migrate data`,
      `GET /api/deploy/dashboard, figures: change failure rate ${cfr == null ? 'not available' : cfr.toFixed(1) + ' %'} of Production deploys over 180 days`,
      `GET /api/deploy/dashboard, flow: ${node('held')} cards on Staging only, ${node('wait-staging')} not deployed yet, ${node('approval')} waiting for approval`,
      `GET /api/deploy/dashboard, tiles: ${name(drift.p)} ${ENVS[drift.e]} is ${drift.drift} releases behind`,
      'GET /api/v1/management/host-releases: the host of one Production environment is one release behind stable, install pending',
      `GET /api/deploy/dashboard, flow by project: ${waiting.map(w => `${w.name} ${w.total}`).join(', ')} cards not live`,
    ],
    headline: `Failures cluster at one step: ${failed} runs of ${badNames} failed at migrate data in two weeks, and ${node('held')} cards sit on Staging.`,
    judgement: `The failed runs are not spread evenly. ${badNames} account for a cluster between ${dayOf(lo)} and ${dayOf(hi)}, nearly all at step migrate data, which points at one cause and not at flaky deploys. `
      + 'Change failure rate and time to restore are the two figures to watch; deploy frequency is healthy. '
      + `Most cards that are not live wait on Staging without a Production environment or promotion (${waiting.map(w => w.name).join(', ')}), not on an approval. `
      + `${name(drift.p)} is the furthest behind in Production. One host is a release behind stable; deploys to it should wait for the pending install.`,
    order: { views: ['timeline', 'figures', 'flow', 'overview'] },
    figures: ['cfr', 'mttr'],
    emphasis: [{ target: tile(name(drift.p), ENVS[drift.e]), weight: 'strong' }, ...waiting.map(w => ({ target: tile(w.name, 'Staging'), weight: 'normal' })).filter(e => ds.cells.some(c => c.setUp && name(c.p) === e.target.project && c.e === 1)),
      { target: { view: 'flow', node: 'held' }, weight: 'strong' }],
    annotations: [
      { id: 'cluster', target: { view: 'timeline', from: dayOf(lo), to: dayOf(hi) }, tone: 'bad', title: 'Failure cluster at migrate data', text: `${failed} of ${total} runs of ${badNames} failed or rolled back in this range, most at the same step.`,
        blocks: 'Production deploys of both projects until the migration is fixed', next: 'Open a card: add a migration dry run to preflight, then rerun the failed releases.', factRefs: [0] },
      { id: 'cfr', target: { view: 'figures', figure: 'cfr' }, tone: 'warn', title: 'Above the 5 % band', text: 'The cluster alone lifts the change failure rate; without it the rate is inside the band.', factRefs: [0, 1] },
      { id: 'held', target: { view: 'flow', node: 'held' }, tone: 'info', title: 'Held on Staging', text: `${node('held')} cards reached Staging and went no further; ${waiting[0] ? waiting[0].name + ' holds ' + waiting[0].counts[1] : 'a few projects hold most'}.`,
        next: 'Set up Production for these projects or mark them Staging only, so the flow stops counting them as waiting.', factRefs: [2, 5] },
      { id: 'drift', target: tile(name(drift.p), ENVS[drift.e]), tone: 'warn', title: `${drift.drift} releases behind`, text: 'The largest drift in Production; each further release makes the next deploy riskier.', next: 'Promote the newest Staging release.', factRefs: [3] },
      { id: 'host-release', target: tile(name(badTile.p), ENVS[badTile.e]), tone: 'warn', title: 'Host release behind stable', text: 'The host this environment runs on is one release behind stable; the install is pending.',
        blocks: `Deploys of ${name(badTile.p)} to ${ENVS[badTile.e]}`, next: 'Install the pending host release, then rerun the held deploys.', factRefs: [4] },
    ],
    goals: [{ id: 'cfr-band', title: 'Change failure rate at or below 5 % for four weeks in a row', done: 1, total: 4, steps: [
      { kind: 'card', title: 'Add a migration dry run to the preflight step', detail: 'Catches the failure of the cluster before the switch; proposed for the two projects first.', state: 'proposed' },
      { kind: 'operation', title: 'Install the pending host release', detail: 'Brings the host back to stable so deploys to it stop being held.', state: 'open' },
    ] }],
  };

  // Rejected on purpose: markup in a text, an unknown key and a project that is not in the facts.
  const rejected = { ...warden, id: 'operations-warden-deploy-rejected', revision: 5,
    headline: 'Production runs <b>without</b> a restorable backup.', style: { tile: 'background: red' },
    annotations: [{ ...warden.annotations[0], target: tile('payments-legacy') }] };

  return { 'operations-warden': warden, 'reliability-engineer': reliability, rejected };
}
