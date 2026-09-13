import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

interface WatchPath {
  name: string;
}

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test('Execution settings show subject definition, manifest state, and justified override guard in both themes', async ({ page, devBackend }) => {
  await page.setViewportSize({ width: 1400, height: 1200 });
  const response = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(response.ok).toBe(true);
  const projects = await response.json() as WatchPath[];
  const project = projects.find(item => /agent studio worktree/i.test(item.name)) ?? projects[0];
  expect(project).toBeTruthy();

  await page.route('**/api/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/crash-recovery/pending', route =>
    route.fulfill({ json: { pending: [] } }));
  await page.goto(`/#/projects/${slugFor(project.name)}/settings`);
  const execution = page.getByTestId('project-execution-definition');
  await expect(execution).toBeVisible();
  await expect(execution.getByText('Valid repository definition')).toBeVisible();
  await expect(execution.getByTestId('execution-repository-definition')).toContainText('schemaVersion: 1');

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
    await execution.screenshot({ path: join(results!, `execution-settings-${theme}--real.png`) });
  }
});
