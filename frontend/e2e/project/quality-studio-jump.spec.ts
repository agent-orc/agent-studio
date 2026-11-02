import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme, type Theme } from '../helpers/theme';

type State = 'not-connected' | 'unreachable' | 'version-too-old' | 'not-registered' | 'ready';
const states: State[] = ['not-connected', 'unreachable', 'version-too-old', 'not-registered', 'ready'];
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '../../test-results/quality-studio');
let projectName = '';

test.beforeAll(async () => {
  fs.mkdirSync(results, { recursive: true });
  const projects = await api<{ name: string }[]>('/api/watch-paths');
  expect(projects.length).toBeGreaterThan(0);
  projectName = projects[0].name;
});

function slug(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function prepare(page: Page, state: State, theme: Theme, role: 'owner' | 'operator' | 'viewer' = 'owner'): Promise<void> {
  const authStatus = {
    profile: 'local', bootstrapRequired: false, authenticated: true,
    user: { id: 'user-1', username: 'user', displayName: 'User', role, projects: ['*'], disabled: false, mustChangePassword: false },
  };
  await page.route('**/api/auth/status', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(authStatus) }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(authStatus) }));
  await page.route('**/api/integrations/quality-studio/status?*', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({
      state, configured: state !== 'not-connected', owner: role === 'owner',
      repositoryId: state === 'not-connected' ? null : 'proj-002',
    }),
  }));
  await page.goto(`/#/projects/${slug(projectName)}/quality-studio`);
  await setTheme(page, theme);
  await expect(page.getByTestId('quality-studio-panel')).toBeVisible();
}

for (const role of ['viewer', 'operator'] as const) {
  test(`${role} does not see an unconfigured integration`, async ({ page }) => {
    await prepare(page, 'not-connected', 'light', role);
    await expect(page.getByTestId('project-shell-rail-quality-studio')).toHaveCount(0);
    await expect(page.locator('[data-testid^="studio-explorer-project-quality-"]')).toHaveCount(0);
  });
}

for (const theme of ['light', 'dark'] as const) {
  for (const state of states) {
    test(`${state} panel and Explorer row in ${theme}`, async ({ page }) => {
      await prepare(page, state, theme);
      const panel = page.getByTestId('quality-studio-panel');
      await expect(panel.locator('[data-state]')).toHaveAttribute('data-state', state);
      if (state === 'ready') await expect(page.getByTestId('quality-studio-open')).toBeEnabled();
      else await expect(page.getByTestId('quality-studio-open')).toBeDisabled();
      await expect(page.getByTestId('project-shell-rail-quality-studio')).toBeVisible();
      await panel.screenshot({ path: path.join(results, `quality-studio-${state}-${theme}--mocked.png`) });
      const project = page.getByTestId(`studio-explorer-project-row-${projectName}`);
      await expect(project).toBeVisible();
      if (!(await project.locator('.studio-tree-children').count()))
        await page.getByTestId(`studio-explorer-project-${projectName}`).click();
      await expect(page.getByTestId(`studio-explorer-project-quality-${projectName}`)).toBeVisible();
      if (state === 'ready') {
        await page.evaluate(() => {
          (window as Window & { qualityOpen?: string; qualityFeatures?: string }).qualityOpen = '';
          window.open = ((url: string | URL, _target?: string, features?: string) => {
            (window as Window & { qualityOpen?: string; qualityFeatures?: string }).qualityOpen = String(url);
            (window as Window & { qualityOpen?: string; qualityFeatures?: string }).qualityFeatures = features;
            return null;
          }) as typeof window.open;
        });
        await page.getByTestId('quality-studio-open').click();
        expect(await page.evaluate(() => (window as Window & { qualityOpen?: string }).qualityOpen))
          .toContain('/api/integrations/quality-studio/open?project=');
        expect(await page.evaluate(() => (window as Window & { qualityFeatures?: string }).qualityFeatures)).toBe('noopener');
      }
    });
  }
}
