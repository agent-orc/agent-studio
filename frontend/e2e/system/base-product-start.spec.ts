import { expect, test } from '@playwright/test';

/**
 * AGT-3966 base suite: one end-to-end start of the product. The prebuilt
 * backend and frontend start on isolated ports (run-prebuilt-e2e.sh), and the
 * main path works against the real backend without route mocks: the API is
 * healthy, a card is created in the isolated project, and the board shows it.
 * Every merge-window run executes this spec; keep it short.
 */
test('the product starts and its board shows a card created through the API', async ({ page, request }) => {
  test.setTimeout(120_000);
  const backend = process.env.PW_BACKEND_URL;
  test.skip(!backend, 'Needs the prebuilt backend that npm run e2e starts.');

  expect((await request.get(`${backend}/healthz`)).ok()).toBe(true);
  const paths = await (await request.get(`${backend}/api/watch-paths`)).json() as { path: string }[];
  expect(paths.length).toBeGreaterThan(0);

  const title = `Base suite product start ${Date.now()}`;
  const created = await request.post(`${backend}/api/tasks`, {
    headers: { 'X-Client-Id': 'local-default' },
    data: { title, watchPath: paths[0].path, agent: 'claude', cliType: 'claude', targetState: '0-backlog' },
  });
  expect(created.ok(), await created.text()).toBe(true);

  const pageErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(`${error.name}: ${error.message}`));
  await page.goto('/');
  await expect(page.locator('[data-studio="root"]')).toBeVisible({ timeout: 60_000 });
  const backlog = page.getByRole('region', { name: 'Backlog cards' });
  await expect(backlog.getByRole('heading', { name: title })).toBeVisible({ timeout: 60_000 });
  expect(pageErrors).toEqual([]);
});
