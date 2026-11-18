// The service worker answers every navigation that matches navigationUrls
// with index.html. Backend documents opened in a new tab (batch run log,
// isolation evidence, task files) must reach the backend instead (AGT-3868).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { Generator } from '@angular/service-worker/config';

const config = JSON.parse(readFileSync(new URL('../ngsw-config.json', import.meta.url), 'utf8'));
const emptyFilesystem = { list: async () => [], read: async () => '', hash: async () => '' };
const manifest = await new Generator(emptyFilesystem, '/').process(config);
const patterns = manifest.navigationUrls.map(({ positive, regex }) => ({ positive, regex: new RegExp(regex) }));

// Mirrors AppVersion.isNavigationUrl in @angular/service-worker: the path
// (without query) must match an include pattern and no exclude pattern.
function servedAsApp(url) {
  const { pathname } = new URL(url, 'http://studio.invalid');
  return patterns.some(p => p.positive && p.regex.test(pathname))
    && !patterns.some(p => !p.positive && p.regex.test(pathname));
}

const backendDocuments = [
  '/api/projects/Demo%20App/batch-gate/run-log?batchId=demo-report-batch-1&runId=demo-report-run-1',
  '/api/projects/Demo%20App/batch-gate/run-evidence?batchId=demo-report-batch-1&runId=demo-report-run-1',
  '/api/projects/Demo%20App/batch-gate/isolation-evidence?parentBatchId=demo-report-batch-1&taskKey=DEMO-20',
  '/api/projects/Demo%20App/proposals/evidence/results/proposal.png',
  '/api/tasks/DEMO-20/files/logs/results/status.md?view=raw',
  '/api/tasks/DEMO-20/files/logs/results',
  '/api/v1/management/backups/backup-1/contents',
  '/api',
  '/hubs/board',
  '/hubs',
  '/healthz',
  '/healthz/deep',
  '/readyz',
];

for (const url of backendDocuments) {
  test(`a new-tab navigation to ${url} reaches the backend`, () => {
    assert.equal(servedAsApp(url), false);
  });
}

for (const url of ['/', '/board', '/projects/Demo%20App/settings', '/apiary', '/hubspot']) {
  test(`an app route ${url} still opens the Studio shell offline`, () => {
    assert.equal(servedAsApp(url), true);
  });
}
