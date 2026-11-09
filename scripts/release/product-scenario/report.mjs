// Writes the run report of one product scenario execution: product.json for
// machines and product.html for people. Besides the per-step rows it lifts out
// the two things the card asks to see at a glance: the designer feedback that
// reached the fix round and the actions Studio refused. Values are redacted
// before they are written, so a password or one-time link stays out.
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

import { redact } from '../../../tools/studio-op/src/credential.mjs';
import { overallStatus } from './runner.mjs';

export const REPORT_SCHEMA_VERSION = 1;

/** Builds the report object; pure so tests can check it without the disk. */
export function buildReport({ scenario, run, steps, people = [], feedback = null, refusals = [] }) {
  return {
    schemaVersion: REPORT_SCHEMA_VERSION,
    scenario: scenario.id,
    runId: run.runId,
    description: scenario.description,
    studio: run.studioOrigin,
    platform: run.platform,
    project: run.project,
    startedAtUtc: run.startedAtUtc,
    endedAtUtc: run.endedAtUtc,
    durationSeconds: run.durationSeconds,
    // The playthrough verdict covers setup and the scenario; cleanup is reported on its own.
    status: overallStatus(steps.filter(step => step.phase !== 'teardown')),
    teardownStatus: steps.some(step => step.phase === 'teardown')
      ? overallStatus(steps.filter(step => step.phase === 'teardown')) : 'not-run',
    people,
    totals: sumUsage(steps),
    feedback,
    refusals,
    steps,
  };
}

/** Sums the per-step meter deltas. */
export function sumUsage(steps) {
  let modelTokens = 0;
  let computeCoreHours = 0;
  let metered = false;
  for (const step of steps) {
    if (!step.usage) continue;
    metered = true;
    modelTokens += step.usage.modelTokens;
    computeCoreHours += step.usage.computeCoreHours;
  }
  return metered ? { modelTokens, computeCoreHours: Math.round(computeCoreHours * 10000) / 10000 } : null;
}

export async function writeReport(directory, report, secrets = []) {
  await mkdir(directory, { recursive: true });
  const json = scrub(`${JSON.stringify(report, null, 2)}\n`, secrets);
  await writeFile(path.join(directory, 'product.json'), json, 'utf8');
  await writeFile(path.join(directory, 'product.html'), scrub(renderHtml(report), secrets), 'utf8');
}

export function renderHtml(report) {
  const rows = report.steps.map(step => `<tr class="status-${escape(step.status)}">
<th scope="row">${escape(step.title)}<br><small>${escape(step.phase)}${step.actor ? `, ${escape(step.actor)}` : ''}</small></th>
<td><span class="badge">${escape(step.status)}</span></td>
<td>${escape(time(step.startedAtUtc))}</td>
<td class="num">${step.durationSeconds}</td>
<td class="num">${step.usage ? step.usage.modelTokens : ''}</td>
<td class="num">${step.usage ? step.usage.computeCoreHours : ''}</td>
<td>${evidenceList(step)}</td>
</tr>`).join('\n');
  const shots = report.steps.filter(step => step.screenshots.length).map(step => `<section>
<h3>${escape(step.title)}</h3><div class="shots">${step.screenshots.map(shot => `<figure>
<a href="${escape(shot.file)}"><img src="${escape(shot.file)}" alt="${escape(shot.caption)}" loading="lazy"></a>
<figcaption>${escape(shot.caption)}</figcaption></figure>`).join('\n')}</div></section>`).join('\n');
  const people = report.people.length
    ? `<ul>${report.people.map(person => `<li>${escape(person.title)}: ${escape(person.login)} (${escape(person.role)}${person.productionApproval ? ', production approval' : ''})</li>`).join('')}</ul>`
    : '<p>No people were invited.</p>';
  const feedback = report.feedback
    ? `<dl>${Object.entries(report.feedback).map(([key, value]) => `<dt>${escape(key)}</dt><dd>${escape(typeof value === 'string' ? value : JSON.stringify(value))}</dd>`).join('')}</dl>`
    : '<p>No feedback was sent.</p>';
  const refusals = report.refusals.length
    ? `<table><thead><tr><th>Person</th><th>Action</th><th>Answer</th><th>Refused</th></tr></thead><tbody>${report.refusals.map(item => `<tr class="status-${item.refused ? 'passed' : 'failed'}">
<td>${escape(item.actor)}</td><td>${escape(item.action)}<br><small>${escape(item.request)}</small></td>
<td class="num">${escape(item.status)} ${escape(item.code ?? '')}</td><td><span class="badge">${item.refused ? 'yes' : 'no'}</span></td></tr>`).join('\n')}</tbody></table>`
    : '<p>No refused actions were tried.</p>';
  const totals = report.totals
    ? `<span class="num">${report.totals.modelTokens}</span> model tokens, <span class="num">${report.totals.computeCoreHours}</span> core-hours`
    : 'not metered';
  return `<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Product scenario ${escape(report.status)}</title>
<style>
:root{color-scheme:light dark;--fg:#182033;--bg:#f7f8fb;--surface:#fff;--line:#ccd3de;--muted:#525c6e;
--passed:#dff3e4;--failed:#fbe1e1;--skipped:#eceef2}
@media(prefers-color-scheme:dark){:root{--fg:#eef0f5;--bg:#15171c;--surface:#1d2027;--line:#414855;--muted:#a9b1bf;
--passed:#1f3a28;--failed:#4a2326;--skipped:#2b2f37}}
body{font:16px system-ui;margin:2rem auto;max-width:72rem;padding:0 1rem;color:var(--fg);background:var(--bg)}
table{border-collapse:collapse;width:100%;background:var(--surface)}th,td{border:1px solid var(--line);padding:.6rem;text-align:left;vertical-align:top}
.num{font-variant-numeric:tabular-nums;text-align:right}dl{margin:0}dt{color:var(--muted);font-size:.85rem}dd{margin:0 0 .3rem;overflow-wrap:anywhere}
.badge{padding:.1rem .5rem;border-radius:1rem;background:var(--skipped)}
.status-passed .badge{background:var(--passed)}.status-failed .badge{background:var(--failed)}
.shots{display:grid;grid-template-columns:repeat(auto-fill,minmax(18rem,1fr));gap:1rem}figure{margin:0}
img{width:100%;border:1px solid var(--line)}figcaption{color:var(--muted);font-size:.9rem}
</style></head><body>
<h1>${escape(report.description)}</h1>
<p>Result: <strong>${escape(report.status)}</strong>. Teardown: ${escape(report.teardownStatus)}. Run ${escape(report.runId)}. Studio ${escape(report.studio)} on ${escape(report.platform)},
project ${escape(report.project)}.</p>
<p>Started ${escape(time(report.startedAtUtc))}, <span class="num">${report.durationSeconds}</span> s. Used ${totals}.</p>
<h2>People</h2>${people}
<table><thead><tr><th>Step</th><th>Status</th><th>Started (UTC)</th><th>Seconds</th><th>Tokens</th><th>Core-hours</th><th>Evidence</th></tr></thead>
<tbody>${rows}</tbody></table>
<h2>Feedback that reached the fix round</h2>${feedback}
<h2>Refused actions</h2>${refusals}
<h2>Screenshots</h2>${shots || '<p>No screenshots were taken.</p>'}
</body></html>
`;
}

function evidenceList(step) {
  const items = Object.entries(step.evidence).map(([key, value]) =>
    `<dt>${escape(key)}</dt><dd>${escape(typeof value === 'string' ? value : JSON.stringify(value))}</dd>`);
  if (step.reason) items.unshift(`<dt>reason</dt><dd>${escape(step.reason)}</dd>`);
  return items.length ? `<dl>${items.join('')}</dl>` : '';
}

// Shorter values would match ordinary words; generated passwords and links are longer.
const MIN_SCRUBBED_LENGTH = 8;

function scrub(text, secrets) {
  let value = text;
  for (const secret of secrets.filter(item => item && item.length >= MIN_SCRUBBED_LENGTH))
    value = value.split(secret).join('[redacted]');
  return redact(value);
}

function time(iso) {
  return iso ? iso.replace('T', ' ').replace(/\.\d+Z$/, 'Z') : '';
}

function escape(value) {
  return String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]);
}
