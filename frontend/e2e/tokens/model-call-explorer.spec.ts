import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const source = process.env.MODEL_CALL_TEST_URL;
const credential = process.env.MODEL_CALL_TEST_READ_TOKEN;
const restrictedSession = process.env.MODEL_CALL_TEST_RESTRICTED_SESSION;
const project = 'coding-night';
const otherProject = 'separate-project';
const from = '2026-11-27T21:52';
const until = '2026-11-28T09:12:07.23';
const windowQuery = `fromMs=${Date.parse(from + 'Z')}&untilMs=${Date.parse(until + 'Z')}`;

test('Model Calls drills from the measured night to a response in an isolated Task Server', async ({ page, request }) => {
  test.skip(!source || !credential || !restrictedSession, 'Requires a standalone Task Server seeded with the validated coding night.');
  test.setTimeout(210_000);
  page.setDefaultTimeout(40_000);
  const headers = {
    Authorization: `Bearer ${credential}`,
    'X-Task-Protocol-Version': '2',
    'X-Task-Client-Version': '1.0.0',
  };
  const callsPath = (scope: string) => `/api/v1/projects/${scope}/model-calls`;
  const get = (path: string) => request.get(source + path, { headers });
  await page.route('**/api/v1/projects/*/model-calls/*', async route => {
    const url = new URL(route.request().url());
    const upstream = await get(url.pathname + url.search);
    await route.fulfill({ response: upstream });
  });
  await page.route('**/api/v1/projects', async route => {
    const response = await get('/api/v1/projects');
    const projects: { projectId: string; name: string; workspaceId: string; taskKeyPrefix: string }[] = await response.json();
    // The existing shell consumes the Studio registry shape. The ledger rows
    // and access decisions come from the standalone server without adaptation.
    await route.fulfill({ json: projects.map(item => ({
      id: item.projectId, displayName: item.name, workspaceId: item.workspaceId,
      shortCode: item.taskKeyPrefix, archived: false, sourceType: 'server',
    })) });
  });

  const unexpectedErrors: string[] = [];
  page.on('pageerror', error => unexpectedErrors.push(error.message));
  await page.goto('/#/workspace/settings/tokens');
  const entry = page.getByTestId('token-usage-nav-model-calls');
  await expect(entry).toBeVisible({ timeout: 45_000 });
  await entry.click();
  const explorer = page.getByTestId('model-call-explorer');
  await explorer.getByLabel('Project').selectOption(project);
  await explorer.getByLabel('From (UTC)').fill(from);
  await explorer.getByLabel('Until (UTC)').fill(until);
  await explorer.getByRole('button', { name: 'Apply filters' }).click();
  await expect(explorer.getByLabel('Window totals')).toContainText('19,774');
  await expect(explorer.getByLabel('Window totals')).toContainText('2,639,640,822');
  await expect(explorer.getByLabel('Window totals')).toContainText('4,592');
  await expect(explorer.getByLabel('Window totals')).toContainText('924');

  await explorer.getByLabel('Card key').fill('AGT-4249');
  await explorer.getByRole('button', { name: 'Apply filters' }).click();
  await expect(explorer.getByRole('button', { name: /^AGT-/ })).toHaveCount(1);
  await explorer.getByRole('button', { name: /^AGT-4249/ }).click();
  const run = explorer.getByRole('button', { name: /^Run 1 / });
  await run.click();
  await explorer.getByRole('button', { name: /^Response 1 / }).click();
  const detail = explorer.getByRole('region', { name: 'Response detail' });
  await expect(detail).toContainText('response time observed');
  await expect(detail).toContainText('codex response record');
  await expect(detail).toContainText('inferred export turn join');
  await expect(detail).toContainText('Unknown');
  await expect(detail.getByRole('link', { name: 'Source card evidence' })).toBeVisible();

  const group = await (await get(`${callsPath(project)}/query?${windowQuery}&card=AGT-4249&run=1`)).json();
  const id = group.responses.find((response: { ordinal: number }) => response.ordinal === 1).id;
  const other = await (await get(`${callsPath(otherProject)}/query?${windowQuery}`)).json();
  expect(other.totals.calls).toBe(0);
  expect((await get(`${callsPath(otherProject)}/${id}`)).status()).toBe(404);
  for (const target of [`query?${windowQuery}`, id]) {
    const response = await request.get(`${source}${callsPath(project)}/${target}`, {
      headers: { ...headers, 'X-Studio-Session-Token': restrictedSession! },
    });
    expect(response.status()).toBe(403);
  }

  const results = process.env.JOB_RESULTS_DIR ?? 'test-results';
  mkdirSync(results, { recursive: true });
  for (const [device, viewport] of [
    ['desktop', { width: 1440, height: 900 }],
    ['phone', { width: 390, height: 844 }],
  ] as const) {
    await page.setViewportSize(viewport);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await detail.getByRole('heading', { name: 'Response 1' }).scrollIntoViewIfNeeded();
      expect(await explorer.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
      await page.screenshot({ path: join(results, `model-calls-${device}-${theme}--real.png`) });
    }
  }
  expect(unexpectedErrors).toEqual([]);
});
