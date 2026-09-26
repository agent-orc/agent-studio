import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { setTheme } from '../helpers/theme';

test('Execution settings show unused-cache warning and justified override guard in both themes', async ({ page }) => {
  const project = 'cache-demo';
  await page.setViewportSize({ width: 1400, height: 1200 });
  await page.route('**/api/**', route => route.fulfill({ json: [] }));
  await page.route(/\/api\/(?:jobs|tasks)\/grouped/, route => route.fulfill({ json: {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [], completed: [], archive: [],
  } }));
  await page.route('**/api/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.route('**/api/watch-paths**', route => route.fulfill({ json: [
    { name: project, path: '/tasks', rootPath: '/repo', repositoryPath: '/repo' },
  ] }));
  await page.route(`**/api/projects/${project}/workbenches**`, route =>
    route.fulfill({ json: { items: [] } }));
  await page.route(`**/api/projects/${project}/execution`, route => route.fulfill({ json: {
    repositoryDefinition: 'schemaVersion: 1\nstack: [dotnet]',
    definitionSha256: '1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef',
    valid: true,
    issues: [],
    preparationWarnings: [{
          code: 'cache-block-unused',
          block: 'nuget',
          consecutiveRuns: 3,
          message: `The nuget block of ${project} is bound but unused for 3 consecutive preparations; the prepare script redirects or unsets NUGET_PACKAGES.`,
    }],
    lastManifest: {
      completedAtUtc: '2026-09-19T01:00:00Z', durationMs: 100, succeeded: true,
      caches: [{ block: 'nuget', key: 'abcdef1234567890', state: 'unused', unusedRunCount: 3 }],
    },
    override: null,
    source: 'subject-commit',
  } }));
  const settings = {
    autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'never',
    pickupMode: 'manual', executionLocation: 'local', integrationBranch: 'develop',
  };
  await page.route('**/api/projects/settings', route => route.fulfill({ json: { [project]: settings } }));
  await page.route(`**/api/projects/${project}/snapshot`, route => route.fulfill({ json: { settings } }));
  await page.route('**/api/runner/status**', route => route.fulfill({ json: { projects: {} } }));
  await page.route('**/api/cli/usage**', route => route.fulfill({ json: { items: [] } }));
  await page.route('**/api/cli/quota**', route => route.fulfill({ json: { snapshots: [] } }));
  await page.addInitScript(projectName => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName, section: 'settings' }],
      activeKey: `hub:${projectName}`,
    }));
  }, project);
  await page.goto('/');
  const execution = page.getByTestId('project-execution-definition');
  await expect(execution).toBeVisible();
  await expect(execution.getByText('Valid repository definition')).toBeVisible();
  await expect(execution.getByTestId('execution-repository-definition')).toContainText('schemaVersion: 1');
  await expect(execution.getByTestId('execution-preparation-warnings'))
    .toContainText('redirects or unsets NUGET_PACKAGES');

  const save = execution.getByTestId('execution-save-override');
  await execution.getByText('Justified operator override').click();
  await expect(save).toBeDisabled();
  await execution.getByTestId('execution-override-justification')
    .fill('Temporary compatibility exception while the repository change is reviewed.');
  await expect(save).toBeEnabled();

  const results = process.env['JOB_RESULTS_DIR'];
  expect(results, 'JOB_RESULTS_DIR is required for durable task evidence').toBeTruthy();
  mkdirSync(results!, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await execution.screenshot({ path: join(results!, `execution-settings-unused-cache-${theme}--mocked.png`) });
  }
});
