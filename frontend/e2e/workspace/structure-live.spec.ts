import { test, expect, type Locator, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { liveSourcesFixture, liveStructureFixture } from '../../src/app/features/structure/live/live-structure.fixtures';

// AGT-4024: the live structure view in Operations, Structure. Mocked; role-named fixtures only.
const results = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');
const LAYERS = ['perf', 'network', 'storage', 'backup', 'warnings'] as const;
const API = 'component:studio-server:api';
const REVIEW_3 = 'machine:review-3';
const STUDIO = 'machine:studio-server';

async function mockApi(page: Page, sources: Partial<Record<'backups' | 'hosts' | 'environment', number>> = {}): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'structure' }], activeKey: 'structure',
  })));
  await page.context().route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.includes('/api/v1/operations/structure/library')) return json({ registries: [], consumers: [] });
    if (url.includes('/api/v1/operations/structure')) return json(liveStructureFixture);
    if (url.includes('/api/operator/backups')) return json(liveSourcesFixture.backup, sources.backups ?? 200);
    // The Studio shell reads the same list for its provider status, so each entry carries the full wire shape.
    if (url.includes('/api/v1/management/remote-hosts')) return json(liveSourcesFixture.runners!.map(runner => ({
      ...runner, instanceId: `${runner.runnerId}-1`, protocolVersion: 1, capabilities: [], hostAdmission: { state: 'admitted' } })), sources.hosts ?? 200);
    if (url.includes('/api/v1/management/environment')) return json(liveSourcesFixture.environment, sources.environment ?? 200);
    if (url.includes('/api/v1/studio/auth/status')) return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (url.includes('/api/v1/management/credential-inventory')) return json({ generatedAt: '2026-11-02T00:00:00Z', items: [] });
    if (url.includes('/api/usage/cockpit')) return json({ workspaceId: 'workspace', clis: [], cost: null });
    if (url.includes('/api/cli/usage')) return json({ at: '2026-11-02T00:00:00Z', sessions: [] });
    if (url.includes('/api/cli/quota')) return json({ at: '2026-11-02T00:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/v1/studio/board')) return json({ backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], codeNotComplete: [], autoReview: [], review: [], humanReview: [], escalated: [], completed: [], archive: [] });
    if (url.includes('/api/tasks/archive')) return json({ items: [], total: 0, offset: 0, limit: 50 });
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    if (url.includes('/api/environment')) return json({ isDev: false, devTools: {} });
    if (url.includes('/api/workbenches')) return json({ projectName: null, count: 0, currentCount: 0, historyCount: 0, items: [] });
    return json([]);
  });
}

async function setTheme(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.evaluate(value => {
    document.documentElement.dataset['studioTheme'] = value;
    try { localStorage.setItem('atp.studio.theme', value); } catch { /* optional */ }
  }, theme);
}

/** The camera rests when the probe of a node stops changing; its box is where the node is on screen. */
async function box(view: Locator, id: string): Promise<{ x: number; y: number; w: number; h: number; level: number }> {
  const probe = view.locator(`[data-testid="live-node"][data-node-id="${id}"]`);
  await expect(probe).toHaveCount(1);
  await expect(view.getByTestId('live-canvas')).toHaveAttribute('data-busy', 'false');
  let last = '';
  await expect.poll(async () => {
    const now = `${await view.getByTestId('live-canvas').getAttribute('data-busy')}|${await probe.getAttribute('data-box')}|${await probe.getAttribute('data-level')}`;
    const stable = now === last && now.startsWith('false');
    last = now;
    return stable;
  }, { intervals: [120, 120, 200, 300], timeout: 10_000 }).toBe(true);
  const [x, y, w, h] = (await probe.getAttribute('data-box'))!.split(',').map(Number);
  return { x, y, w, h, level: Number(await probe.getAttribute('data-level')) };
}

/** A real pointer click on the canvas, on the header strip of the node. */
async function clickNode(page: Page, view: Locator, id: string): Promise<void> {
  const node = await box(view, id);
  const canvas = (await view.getByTestId('live-canvas').boundingBox())!;
  await page.mouse.click(canvas.x + node.x + Math.min(node.w / 2, 40), canvas.y + node.y + Math.min(node.h / 2, 12));
}

async function open(page: Page, query = ''): Promise<Locator> {
  await page.goto(`/#/workspace/operations/structure${query}`);
  const view = page.getByTestId('live-structure');
  await expect(view).toBeVisible();
  await box(view, STUDIO);
  return view;
}

const shot = (page: Page, name: string) => page.screenshot({ path: path.join(results, `${name}.png`) });

/** Leaves exactly the given layers on, through the visible switches. */
async function setLayers(view: Locator, on: readonly string[]): Promise<void> {
  for (const layer of LAYERS) {
    const control = view.getByTestId(`live-layer-${layer}`);
    if ((await control.getAttribute('aria-checked') === 'true') !== on.includes(layer)) await control.click();
    await expect(control).toHaveAttribute('aria-checked', String(on.includes(layer)));
  }
}

function watchErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  // The mocked run has no hub; its connection errors are the shell's, every other console error counts.
  page.on('console', message => { if (message.type() === 'error' && !/Failed to load resource|negotiation|connection/i.test(message.text())) errors.push(message.text()); });
  return errors;
}

test('live structure: select a node on the canvas, switch each layer, select from the outline', async ({ page }) => {
  test.setTimeout(240_000);
  fs.mkdirSync(results, { recursive: true });
  const errors = watchErrors(page);
  await mockApi(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  const view = await open(page);
  const details = view.getByTestId('live-details');
  const canvas = view.getByTestId('live-canvas');

  // The real installation, by role: no reported host name is drawn or listed.
  await expect(view.getByTestId('live-outline')).toContainText('Studio server');
  await expect(view.getByTestId('live-outline')).toContainText('Review host 3');
  await expect(view.getByTestId('live-outline')).toContainText('Gate host');
  await expect(view.getByTestId('live-outline')).not.toContainText('review-3');
  await expect(view.getByTestId('live-outline')).not.toContainText('linux-vm');

  // Five labelled switches, each saying On or Off and one fact of its layer.
  for (const layer of LAYERS) {
    const control = view.getByTestId(`live-layer-${layer}`);
    await expect(control).toBeVisible();
    await expect(control).toHaveAttribute('role', 'switch');
    await expect(control).toContainText(await control.getAttribute('aria-checked') === 'true' ? 'On' : 'Off');
  }
  await expect(view).toHaveAttribute('data-layers', 'perf,network,warnings');
  await expect(view.getByTestId('live-layer-fact-perf')).toHaveText('5 of 12 slots busy');
  await expect(view.getByTestId('live-layer-fact-storage')).toHaveText('fullest disk 84%');
  await expect(view.getByTestId('live-layer-fact-backup')).toHaveText('0 of 4 covered');
  await expect(view.getByTestId('live-layer-fact-warnings')).toHaveText('8 open');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await box(view, STUDIO);
    await page.waitForTimeout(150);
    await shot(page, `live-structure-overview-${theme}`);
  }
  await setTheme(page, 'light');

  // Selecting a node with the pointer opens its card and marks its outline row.
  expect((await box(view, API)).level).toBe(1);
  await clickNode(page, view, API);
  await expect(view).toHaveAttribute('data-selected', API);
  await expect(details.getByTestId('live-details-name')).toHaveText('API');
  await expect(details).toHaveAttribute('data-node-id', API);
  await expect(view.locator(`[data-outline-id="${API}"]`)).toHaveAttribute('aria-current', 'true');
  // Every layer has its own section of facts in the card, on or off.
  for (const layer of LAYERS) await expect(details.getByTestId(`live-details-${layer}`)).toBeVisible();
  await expect(details.getByTestId('live-details-storage')).toContainText('layer is off');
  await expect(details.locator('[data-fact="ports"]')).toContainText('not observed');
  await expect(details.locator('[data-fact="deploy"]')).toContainText('not observed');
  await expect(details.locator('[data-fact="version"]')).toContainText('2.0');

  // Each layer alone draws something of its own: the plan differs from the bare structure and from every other layer.
  await page.keyboard.press('Escape');
  await setLayers(view, []);
  await expect(view).toHaveAttribute('data-layers', '');
  await box(view, STUDIO);
  const bare = (await canvas.screenshot()).toString('base64');
  const seen = new Set<string>([bare]);
  for (const layer of LAYERS) {
    await setLayers(view, [layer]);
    await expect(view).toHaveAttribute('data-layers', layer);
    await page.waitForTimeout(150);
    const drawn = (await canvas.screenshot()).toString('base64');
    expect(seen.has(drawn), `${layer} looks like another layer`).toBe(false);
    seen.add(drawn);
  }
  // The switches also answer to the keys 1 to 5.
  await canvas.focus();
  await page.keyboard.press('1');
  await expect(view).toHaveAttribute('data-layers', 'perf,warnings');
  await page.keyboard.press('1');
  await expect(view).toHaveAttribute('data-layers', 'warnings');

  // One machine, zoomed, with each layer on its own.
  await view.locator(`[data-outline-id="${STUDIO}"]`).click();
  await view.locator(`[data-outline-id="${STUDIO}"]`).click();
  await expect(view).toHaveAttribute('data-selected', STUDIO);
  await expect.poll(async () => (await box(view, STUDIO)).level).toBeGreaterThanOrEqual(2);
  await setLayers(view, ['perf', 'network', 'warnings']);
  await page.waitForTimeout(150);
  await shot(page, 'live-structure-machine-zoomed');
  for (const layer of LAYERS) {
    await setLayers(view, [layer]);
    await details.getByTestId(`live-details-${layer}`).scrollIntoViewIfNeeded();
    await page.waitForTimeout(150);
    await shot(page, `live-structure-layer-${layer}`);
  }
  await setLayers(view, ['perf', 'network', 'warnings']);

  // The outline selects on the canvas and brings the node into view; a second click zooms to it.
  const row = view.locator(`[data-outline-id="${REVIEW_3}"]`);
  await row.click();
  await expect(view).toHaveAttribute('data-selected', REVIEW_3);
  await expect(row).toHaveAttribute('aria-current', 'true');
  await expect(details.getByTestId('live-details-name')).toHaveText('Review host 3');
  await expect(details.getByTestId('live-details-warnings')).toContainText('Missing toolchain:playwright');
  const stage = (await canvas.boundingBox())!;
  const revealed = await box(view, REVIEW_3);
  expect(revealed.x).toBeGreaterThanOrEqual(0);
  expect(revealed.y).toBeGreaterThanOrEqual(0);
  expect(revealed.x + revealed.w).toBeLessThanOrEqual(stage.width + 1);
  expect(revealed.y + revealed.h).toBeLessThanOrEqual(stage.height + 1);
  await row.click();
  await expect.poll(async () => (await box(view, REVIEW_3)).level).toBeGreaterThanOrEqual(3);
  await expect(view.getByTestId('live-crumbs')).toContainText('Review host 3');
  await details.evaluate(element => element.parentElement?.scrollTo(0, 0));
  await page.waitForTimeout(150);
  await shot(page, 'live-structure-container-details');

  // Zoom with the keyboard and the wheel: the node gets more facts, never smaller type.
  await page.keyboard.press('Escape');
  await canvas.focus();
  await page.keyboard.press('0');
  const far = await box(view, API);
  expect(far.level).toBe(1);
  await page.mouse.move(stage.x + far.x + far.w / 2, stage.y + far.y + far.h / 2);
  for (let i = 0; i < 6; i++) await page.mouse.wheel(0, -100);
  const near = await box(view, API);
  expect(near.w).toBeGreaterThan(far.w * 1.8);
  expect(near.level).toBeGreaterThan(far.level);
  // The point under the cursor stays under the cursor.
  expect(Math.abs((near.x + near.w / 2) - (far.x + far.w / 2))).toBeLessThan(near.w / 2);
  await page.keyboard.press('+');
  expect((await box(view, API)).w).toBeGreaterThan(near.w);
  await page.keyboard.press('0');
  expect((await box(view, API)).level).toBe(1);
  await expect(view.getByTestId('live-level')).toContainText('Detail level 1 of 4');

  // Nothing changes, so nothing is drawn.
  const frames = await canvas.getAttribute('data-frames');
  await page.waitForTimeout(1500);
  expect(await canvas.getAttribute('data-frames')).toBe(frames);
  expect(errors).toEqual([]);
});

test('live structure: reads that fail show as not observed, and the classic drawing stays one switch away', async ({ page }) => {
  fs.mkdirSync(results, { recursive: true });
  const errors = watchErrors(page);
  await mockApi(page, { backups: 403, hosts: 403, environment: 403 });
  await page.setViewportSize({ width: 1440, height: 900 });
  const view = await open(page);
  await expect(view.getByTestId('live-layer-fact-backup')).toHaveText('not observed');
  await view.locator(`[data-outline-id="${REVIEW_3}"]`).click();
  const details = view.getByTestId('live-details');
  for (const fact of ['uptime', 'restarts', 'ports', 'deploy', 'image'])
    await expect(details.locator(`[data-fact="${fact}"]`)).toContainText('not observed');
  await view.locator('[data-outline-id="backup-target"]').click();
  await expect(details.getByTestId('live-details-name')).toHaveText('Off-host backup target');
  await expect(details.getByTestId('live-details-health')).toContainText('Not observed');

  // The address remembers layers and focus, and restores them.
  await setLayers(view, ['backup']);
  await expect(page).toHaveURL(/mode=live/);
  await expect(page).toHaveURL(/layers=backup/);
  await page.reload();
  await expect(page.getByTestId('live-structure')).toHaveAttribute('data-layers', 'backup');
  await expect(page.getByTestId('live-structure')).toHaveAttribute('data-selected', 'backup-target');

  // Classic drawing: the same data, the earlier view.
  await page.getByTestId('structure-view').getByRole('button', { name: 'Classic' }).click();
  await expect(page.getByTestId('structure-canvas')).toBeVisible();
  await expect(page.getByTestId('live-structure')).toHaveCount(0);
  await expect(page).toHaveURL(/mode=classic/);
  expect(errors).toEqual([]);
});

test.describe('phone', () => {
  test.use({ hasTouch: true, viewport: { width: 390, height: 844 } });

  test('live structure at phone width: layers, tap to select, details as a sheet, no sideways scroll', async ({ page }) => {
    test.setTimeout(120_000);
    fs.mkdirSync(results, { recursive: true });
    const errors = watchErrors(page);
    await mockApi(page);
    const view = await open(page);
    for (const layer of LAYERS) await expect(view.getByTestId(`live-layer-${layer}`)).toBeVisible();
    await expect(view).toHaveAttribute('data-mode', 'tall');
    const overflow = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(await overflow()).toBeLessThanOrEqual(0);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await box(view, STUDIO);
      await page.waitForTimeout(150);
      await shot(page, `live-structure-phone-${theme}`);
    }
    await setTheme(page, 'light');
    const canvas = (await view.getByTestId('live-canvas').boundingBox())!;
    const node = await box(view, STUDIO);
    await page.touchscreen.tap(canvas.x + node.x + node.w / 2, canvas.y + node.y + 10);
    await expect(view).toHaveAttribute('data-selected', STUDIO);
    await expect(view.getByTestId('live-panel')).toHaveAttribute('data-sheet', 'open');
    await expect(view.getByTestId('live-details-name')).toHaveText('Studio server');
    await page.waitForTimeout(200);
    await shot(page, 'live-structure-phone-details');
    await view.getByRole('button', { name: 'Outline' }).click();
    await view.locator(`[data-outline-id="${REVIEW_3}"]`).click();
    await expect(view).toHaveAttribute('data-selected', REVIEW_3);
    await expect(view.getByTestId('live-details-name')).toHaveText('Review host 3');
    expect(await overflow()).toBeLessThanOrEqual(0);
    expect(errors).toEqual([]);
  });
});
