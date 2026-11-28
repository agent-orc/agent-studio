// Combine completed fixture runs without requiring one scenario to run the other.
import { readFile, rename, writeFile } from 'node:fs/promises';
import path from 'node:path';

const dir = process.env.JOB_RESULTS_DIR;
async function read(name) {
  try { return JSON.parse(await readFile(path.join(dir, name), 'utf8')); }
  catch (error) { if (error.code !== 'ENOENT') throw error; return null; }
}
const cell = value => String(value).replace(/\x1b\[[0-9;]*m/g, '').replaceAll('|', '\\|').replace(/\s+/g, ' ').trim();
const table = ['| Step and action | UTC start | Seconds | Outcome and product response | Screenshot |',
  '|---|---|---:|---|---|'];
const report = ['# Click Counter on the local Studio', '',
  'Each command starts the AGT-4239 Production backend on a fresh workspace, serves the built Studio over HTTPS and opens Playwright Chromium. All times are UTC.',
  '', 'The static-web checkout and its Git origin are local. The provider is a deterministic test executable on a project-bound runner. The fixture gate tests and packages the site, promotes its commit to the local origin and posts the release. A project-bound deploy client claims the real Studio job, verifies and serves the downloaded package, probes the declared health URL and reports the observed status. These substitutions exercise release and health handling independently of a card passing review.', ''];
function productResponse(step) {
  const e = step.evidence;
  if (step.id === 'invite' && e.users) return e.users.map(u => u.person + ' joined as ' + u.role +
    (u.productionApprovalProjects.length ? ' with production approval' : '')).join('. ');
  if (step.id === 'wish' && e.card) return 'Project chat HTTP ' + e.productChat.status + '. ' +
    (e.productChat.reply ?? '') + ' Questions: ' + e.questions.join(' ') + ' ' + e.proposal +
    ' Confirmation HTTP ' + e.createStatus + '. ' + e.card + ' (' + e.lane + ').';
  if (step.id === 'delivery' && e.delivery) return e.delivery.key + ' in ' + e.delivery.lane +
    '. Preview ' + (e.delivery.preview?.address ?? 'not observed') + '. ' +
    (e.runnerReadiness ?? []).map(check => check.detail).join(' ');
  if (step.id === 'refusals' && e.answers) return e.answers.join('. ');
  const entries = Object.entries(e).filter(([key]) => !['credentialsPage', 'hostsPage'].includes(key));
  return entries.map(([key, value]) => key + ' = ' + (typeof value === 'object' ? JSON.stringify(value) : value)).join('. ');
}
function row(step, response, screenshots) {
  const seconds = ((Date.parse(step.endedAtUtc) - Date.parse(step.startedAtUtc)) / 1000).toFixed(1);
  const message = [step.status, step.reason, response].filter(Boolean).map(cell).join(' · ');
  report.push('| ' + [cell(step.title), step.startedAtUtc, seconds, message, screenshots || 'Not observed'].join(' | ') + ' |');
}
for (const [name, title] of [['lifecycle', 'AGT-3178 · Application lifecycle'], ['product', 'AGT-3183 · Product team']]) {
  report.push('## ' + title, '', '`scenarios/application-lifecycle/run.sh ' + name + '`', '');
  const data = await read(name + (name === 'product' ? '/product.json' : '/steps.json'));
  if (!data) { report.push('No completed run recorded.', ''); continue; }
  const rows = name === 'lifecycle' ? data : data.steps;
  const counts = ['passed', 'failed', 'skipped'].map(status => rows.filter(row => row.status === status).length + ' ' + status);
  report.push(counts.join(', ') + '. [Full step evidence](' + name + (name === 'product' ? '/product.json' : '/steps.json') + ').', '', ...table);
  if (name === 'lifecycle') {
    for (const step of data) row(step, step.evidence.join(' '), '[View](lifecycle/' + step.screenshot + ')');
  } else {
    for (const step of data.steps) {
      row(step, productResponse(step), step.screenshots.map(shot => '[' + cell(shot.caption) + '](product/' + shot.file + ')').join(' · '));
    }
  }
  report.push('');
}
const pending = path.join(dir, 'report-' + process.pid + '.tmp');
await writeFile(pending, report.join('\n') + '\n');
await rename(pending, path.join(dir, 'report.md'));
