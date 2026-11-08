// Writes the run report of one lifecycle scenario execution: lifecycle.json
// for machines and lifecycle.html for people. Values are redacted before they
// are written, so a token that slipped into an error message stays out.
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

import { redact } from '../../../tools/studio-op/src/credential.mjs';
import { overallStatus } from './runner.mjs';

export const REPORT_SCHEMA_VERSION = 1;

/** Builds the report object; pure so tests can check it without the disk. */
export function buildReport({ scenario, run, steps, addresses, totals }) {
  return {
    schemaVersion: REPORT_SCHEMA_VERSION,
    scenario: scenario.id,
    runId: run.runId,
    description: scenario.description,
    studio: run.studioOrigin,
    platform: run.platform,
    workspace: run.workspace,
    project: run.project,
    startedAtUtc: run.startedAtUtc,
    endedAtUtc: run.endedAtUtc,
    durationSeconds: run.durationSeconds,
    // The playthrough verdict covers setup and the scenario; cleanup is reported on its own.
    status: overallStatus(steps.filter(step => step.phase !== 'teardown')),
    teardownStatus: steps.some(step => step.phase === 'teardown')
      ? overallStatus(steps.filter(step => step.phase === 'teardown')) : 'not-run',
    addresses: { stage: addresses.stage ?? null, production: addresses.production ?? null },
    totals: totals ?? sumUsage(steps),
    steps,
  };
}

/** Sums the per-step meter deltas when no whole-run delta is available. */
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
  await writeFile(path.join(directory, 'lifecycle.json'), json, 'utf8');
  await writeFile(path.join(directory, 'lifecycle.html'), scrub(renderHtml(report), secrets), 'utf8');
}

export function renderHtml(report) {
  const rows = report.steps.map(step => `<tr class="status-${escape(step.status)}">
<th scope="row">${escape(step.title)}<br><small>${escape(step.phase)}</small></th>
<td><span class="badge">${escape(step.status)}</span></td>
<td>${escape(time(step.startedAtUtc))}</td>
<td class="num">${step.durationSeconds}</td>
<td class="num">${step.usage ? step.usage.modelTokens : ''}</td>
<td class="num">${step.usage ? step.usage.computeCoreHours : ''}</td>
<td>${evidenceList(step)}</td>
</tr>`).join('\n');
  const shots = report.steps.flatMap(step => step.screenshots.map(shot => `<figure>
<a href="${escape(shot.file)}"><img src="${escape(shot.file)}" alt="${escape(shot.caption)}" loading="lazy"></a>
<figcaption>${escape(step.title)}: ${escape(shot.caption)}</figcaption></figure>`)).join('\n');
  const link = value => value ? `<a href="${escape(value)}">${escape(value)}</a>` : 'not reached';
  const totals = report.totals
    ? `<span class="num">${report.totals.modelTokens}</span> model tokens, <span class="num">${report.totals.computeCoreHours}</span> core-hours`
    : 'not metered';
  return `<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Lifecycle scenario ${escape(report.status)}</title>
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
workspace ${escape(report.workspace)}, project ${escape(report.project)}.</p>
<p>Started ${escape(time(report.startedAtUtc))}, <span class="num">${report.durationSeconds}</span> s. Used ${totals}.</p>
<p>Stage: ${link(report.addresses.stage)}. Production: ${link(report.addresses.production)}.</p>
<table><thead><tr><th>Step</th><th>Status</th><th>Started (UTC)</th><th>Seconds</th><th>Tokens</th><th>Core-hours</th><th>Evidence</th></tr></thead>
<tbody>${rows}</tbody></table>
<h2>Screenshots</h2><div class="shots">${shots || '<p>No screenshots were taken.</p>'}</div>
</body></html>
`;
}

function evidenceList(step) {
  const items = Object.entries(step.evidence).map(([key, value]) =>
    `<dt>${escape(key)}</dt><dd>${escape(typeof value === 'string' ? value : JSON.stringify(value))}</dd>`);
  if (step.reason) items.unshift(`<dt>reason</dt><dd>${escape(step.reason)}</dd>`);
  return items.length ? `<dl>${items.join('')}</dl>` : '';
}

// Shorter values would match ordinary words; Studio passwords have at least 12 characters.
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
