import { test, expect } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const RESULT_DIR = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : 'test-results';

const platformRules = [
  { id: 'authority-keep', artifactClass: 'Authority', hotCapBytesPerFile: 0, hotBudgetBytesPerTask: 0, refuseAboveBytes: 0, archiveAfterDaysTerminal: null, archiveTaskAfterDaysTerminal: null, deleteAfterDays: null, deleteArchiveAfterDaysTerminal: null, deleteArchiveEnabled: false, neverArchiveLanes: [] },
  { id: 'evidence-stage-2', artifactClass: 'Evidence', hotCapBytesPerFile: 0, hotBudgetBytesPerTask: 0, refuseAboveBytes: 0, archiveAfterDaysTerminal: null, archiveTaskAfterDaysTerminal: 180, deleteAfterDays: null, deleteArchiveAfterDaysTerminal: null, deleteArchiveEnabled: false, neverArchiveLanes: [] },
  { id: 'heavy-stage-1', artifactClass: 'HeavyWorkingData', hotCapBytesPerFile: 10_485_760, hotBudgetBytesPerTask: 67_108_864, refuseAboveBytes: 52_428_800, archiveAfterDaysTerminal: 30, archiveTaskAfterDaysTerminal: 180, deleteAfterDays: null, deleteArchiveAfterDaysTerminal: 730, deleteArchiveEnabled: false, neverArchiveLanes: [] },
  { id: 'runtime-delete', artifactClass: 'Runtime', hotCapBytesPerFile: 0, hotBudgetBytesPerTask: 0, refuseAboveBytes: 0, archiveAfterDaysTerminal: null, archiveTaskAfterDaysTerminal: null, deleteAfterDays: 30, deleteArchiveAfterDaysTerminal: null, deleteArchiveEnabled: false, neverArchiveLanes: [] },
];

test('operator edits, previews, runs, inspects, and restores retention data', async ({ page, devBackend }) => {
  mkdirSync(RESULT_DIR, { recursive: true });
  const paths = await api<Array<{ path: string }>>('/api/watch-paths?includeFixtures=true');
  const watchPath = paths[0]?.path ?? devBackend.workspace;
  const task = await createJob({
    id: `retention-e2e-${Date.now()}`,
    title: 'Retention archived task',
    watchPath,
    targetState: '7-archive',
    fixture: false,
    promptMarkdown: '# Archived task\n\nHot task summary.',
  });
  let version = 0;
  let restored = false;
  const policy = () => ({ scope: 'workspace', version, updatedAt: '2026-09-06T12:00:00Z', updatedBy: version ? 'operator' : 'platform', rules: platformRules, fullBackups: { daily: 7, weekly: 4, monthly: 12 } });
  const action = { kind: 'ArchiveHeavy', ruleId: 'heavy-stage-1', project: 'Agent Studio Worktree', taskKey: task.id, taskId: task.id, stage: 1, bytes: 4096, fileCount: 1, reason: '30 days terminal', lane: '7-archive', terminalAt: '2026-07-01T00:00:00Z' };
  const plan = { plannedAt: '2026-09-06T12:00:00Z', policyVersion: 1, actionCount: 1, totalBytes: 4096, affectedTasks: 1, actions: [action] };
  const manifest = { taskId: task.id, taskKey: task.id, project: 'Agent Studio Worktree', archivedAt: '2026-09-06T12:00:00Z', state: 'cold', totalBytes: 4096, restoredAt: null, tombstonedAt: null, stages: [{ stage: 1, archivedAt: '2026-09-06T12:00:00Z', payloadPath: 'cold.zip', payloadSha256: 'feed', totalBytes: 4096, files: [{ name: 'cli-output.log', size: 4096, sha256: 'abc123' }], policyVersion: 1, archivedBy: 'operator' }] };

  await page.route('**/api/auth/status', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }) }));
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ pending: [] }) }));
  await page.route('**/api/v1/management/status', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ maintenance: { mode: 'normal' } }) }));
  await page.route('**/api/tasks/archive**', async route => {
    const response = await route.fetch();
    const body = await response.json() as { items?: Array<{ id: string; archiveState?: string }> };
    for (const item of body.items ?? []) if (item.id === task.id) item.archiveState = 'cold';
    await route.fulfill({ response, json: body });
  });
  await page.route('**/api/v1/management/backups/full', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ backups: [] }) }));
  await page.route('**/api/v1/management/retention/runs', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/v1/management/retention/schedule', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ enabled: true, serverLocalHour: 3, nextRunAt: '2026-09-09T03:00:00Z' }) }));
  await page.route(`**/api/v1/management/retention/archive/${task.id}/restore`, async route => {
    restored = true; await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ restored: true, taskId: task.id }) });
  });
  await page.route(`**/api/v1/management/retention/archive/${task.id}`, route => route.fulfill({ status: restored ? 404 : 200, contentType: 'application/json', body: restored ? '{}' : JSON.stringify(manifest) }));
  await page.route('**/api/v1/management/retention/policy', async route => {
    if (route.request().method() === 'PUT') version += 1;
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(policy()) });
  });
  await page.route('**/api/v1/management/retention/plan', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(plan) }));
  await page.route('**/api/v1/management/retention/apply', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runId: 'run-1', plan, appliedActions: 1, appliedBytes: 4096, errors: [], warnings: [] }) }));

  await page.addInitScript(() => { localStorage.setItem('atp.flag.vsCodeLayout', '0'); });
  try {
    await page.goto('/#/workspace/settings/retention');
    await dismissDevErrorDialog(page);
    await expect(page.getByTestId('retention-admin')).toBeVisible();
    await page.getByTestId('retention-rule-edit-runtime').click();
    await page.getByTestId('retention-rule-runtime').locator('input[type="number"]').fill('31');
    await page.getByTestId('retention-rule-save').click();
    await expect(page.getByTestId('retention-rules-message')).toContainText('saved');
    await page.getByTestId('retention-preview').click();
    await expect(page.getByTestId('retention-summary')).toContainText('1 tasks');
    await page.getByTestId('retention-apply').click();
    await expect(page.getByTestId('retention-summary')).toContainText('Run report');
    await page.getByTestId('workspace-retention-overlay').evaluate(element => element.scrollTo(0, 0));

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('workspace-retention-overlay').screenshot({ path: join(RESULT_DIR, `retention-${theme}.png`) });
    }
    await page.setViewportSize({ width: 720, height: 900 });
    await page.getByTestId('workspace-retention-overlay').screenshot({ path: join(RESULT_DIR, 'retention-narrow.png') });
    await page.getByTestId('workspace-settings-close').click();
    await page.setViewportSize({ width: 1600, height: 950 });

    const row = page.getByTestId('archive-row').filter({ hasText: 'Retention archived task' });
    await expect(row).toBeVisible(); await expect(row).toContainText('cold'); await row.click();
    await expect(page.getByTestId('archived-task-notice')).toContainText('in cold storage');
    const docsTab = page.getByRole('tab', { name: 'Docs' });
    if (await docsTab.count()) await docsTab.click();
    else {
      await page.getByTestId('pane-tabs-overflow').click();
      await page.getByTestId('pane-tabs-overflow-item-description').click();
    }
    await expect(page.getByTestId('archived-files-manifest')).toContainText('cli-output.log');
    await page.getByTestId('archived-task-restore').click();
    await expect(page.getByTestId('archived-task-notice')).toHaveCount(0);
    await expect(page.getByTestId('archived-files-manifest')).toHaveCount(0);
  } finally {
    await api(`/api/tasks/${encodeURIComponent(task.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' }).catch(() => undefined);
  }
});
