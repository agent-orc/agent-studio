import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';

interface WatchPath { name: string; path: string }
const slugFor = (name: string) => name.trim().toLowerCase()
  .replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

test('project picker keeps Settings when switching projects', async ({ page }) => {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const first = paths[0];
  expect(first).toBeDefined();
  const second = paths[1] ?? { name: 'Second Fixture Project', path: `${first.path}/second-fixture` };
  await page.route('**/api/watch-paths**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([first, second]),
  }));
  await page.goto(`/#/projects/${slugFor(first.name)}/settings`);
  await expect(page.getByTestId('project-settings-panel')).toBeVisible();
  await page.getByTestId('studio-project-picker-trigger').click();
  await page.getByTestId(`studio-project-picker-item-${second.name}`).click();
  await expect(page.getByRole('tab', { name: `${second.name} · Settings` })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('project-settings-panel')).toBeVisible();
});
