import { expect, test } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string }
interface Workspace { projects: { id: string; displayName: string }[] }

function screenshotPath(name: string): string {
  const results = process.env.JOB_RESULTS_DIR ?? path.resolve('test-results');
  fs.mkdirSync(results, { recursive: true });
  return path.join(results, name);
}

test('production board and deferred Project Hub load', async ({ page }) => {
  const watchPaths = await api<WatchPath[]>('/api/watch-paths');
  const workspaces = await api<Workspace[]>('/api/v1/workspaces');
  const project = workspaces.flatMap(workspace => workspace.projects)
    .find(candidate => candidate.displayName === watchPaths[0]?.name);
  expect(project, 'a registered project with a watch path').toBeDefined();

  await page.goto('/');
  await expect(page.getByTestId('studio-board')).toBeVisible();
  await page.goto(`/#/projects/${encodeURIComponent(project!.id)}/overview`);
  await expect(page.getByTestId('project-shell')).toBeVisible();
  await expect(page.getByTestId('project-overview-dashboard')).toBeVisible();
  await page.screenshot({ path: screenshotPath('initial-bundle-project-hub.png') });
});

test('deferred task detail loads from a task URL', async ({ page }) => {
  const [watchPath] = await api<WatchPath[]>('/api/watch-paths');
  expect(watchPath, 'a project watch path').toBeDefined();
  const title = `lazy-view-smoke-${Date.now()}`;
  const task = await createJob({ title, watchPath: watchPath.path, promptMarkdown: '# Lazy detail smoke' });

  try {
    await page.goto(`/?job=${encodeURIComponent(task.id)}&watchPath=${encodeURIComponent(watchPath.path)}`);
    await expect(page.getByTestId('studio-task')).toBeVisible();
    await expect(page.getByTestId('studio-task')).toContainText(title);
    await page.screenshot({ path: screenshotPath('initial-bundle-task-detail.png') });
  } finally {
    await api(`/api/tasks/${encodeURIComponent(task.id)}?watchPath=${encodeURIComponent(watchPath.path)}`, {
      method: 'DELETE',
    });
  }
});
