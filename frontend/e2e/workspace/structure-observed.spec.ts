import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { observedStructure } from '../../src/app/features/structure/structure.fixtures';

// AGT-3954: the existing Structure view with hosts that only report. Role-named fixtures only.
const results = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');

async function mockStructure(page: Page, current: () => typeof observedStructure, reads: { status: number }[]): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'structure' }], activeKey: 'structure',
  })));
  await page.route('**/api/v1/operations/structure/library**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ registries: [], consumers: [] }) }));
  await page.route(/\/api\/v1\/operations\/structure(?:\?|$)/, route => {
    const data = current();
    // The revision is the entity tag, as on the server.
    const status = route.request().headers()['if-none-match'] === `"${data.revision}"` ? 304 : 200;
    reads.push({ status });
    return status === 304
      ? route.fulfill({ status: 304, headers: { etag: `"${data.revision}"` } })
      : route.fulfill({ status: 200, contentType: 'application/json', headers: { etag: `"${data.revision}"` }, body: JSON.stringify(data) });
  });
}

test('hosts that only report show nested, marked and with facts in both themes at 1280 px and 390 px', async ({ page }) => {
  test.setTimeout(180_000);
  fs.mkdirSync(results, { recursive: true });
  const pageErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  let data = observedStructure;
  const reads: { status: number }[] = [];
  await mockStructure(page, () => data, reads);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/#/workspace/operations/structure?level=3');
  const view = page.getByTestId('structure-view');
  const node = (id: string) => view.locator(`[data-node-id="${id}"]`);
  await expect(node('machine:studio-server')).toBeVisible();

  // Three hosts and more than six containers, none of them declared.
  for (const id of ['machine:studio-server', 'machine:home-lab', 'machine:linux-vm', 'machine:runner-host', 'machine:old-host'])
    await expect(node(id)).toBeVisible();
  for (const id of ['component:studio-server:api', 'component:studio-server:web', 'component:studio-server:edge',
    'component:studio-server:engine', 'machine:review-1', 'machine:review-2', 'machine:review-3', 'machine:gate-1',
    'observed-container:runner-host:deploy-stage-api-1'])
    await expect(node(id)).toBeVisible();
  await expect(node('machine:review-1')).toContainText('not declared');
  await expect(node('machine:review-1').locator('.structure-observation-state')).toContainText('Up');
  await expect(node('machine:review-1')).toHaveAttribute('aria-label', /observed, not declared/);
  await expect(node('machine:old-host').locator('.structure-observation-state')).toContainText('Stale');
  await expect(node('machine:spare').locator('.structure-observation-state')).toContainText('Not observed');
  await expect(node('machine:spare').locator('.structure-chip')).toHaveCount(0);
  await expect(node('observed-container:runner-host:deploy-stage-api-1').locator('.structure-observation-state')).toContainText('Down');

  // Container inside virtual machine inside physical machine, from the reports.
  const inside = async (inner: string, outer: string): Promise<boolean> => {
    const a = (await node(inner).locator('xpath=..').boundingBox())!, b = (await node(outer).locator('xpath=..').boundingBox())!;
    return a.x >= b.x && a.y >= b.y && a.x + a.width <= b.x + b.width + 1 && a.y + a.height <= b.y + b.height + 1;
  };
  expect(await inside('machine:review-1', 'machine:linux-vm')).toBe(true);
  expect(await inside('machine:linux-vm', 'machine:home-lab')).toBe(true);
  expect(await inside('component:studio-server:api', 'machine:studio-server')).toBe(true);
  expect(await inside('machine:runner-host', 'machine:studio-server')).toBe(true);
  await expect(view.getByTestId('structure-link-key')).toContainText('Task Server connection');
  await expect(view.getByTestId('structure-link-key')).toContainText('4.5 ms');

  // Facts of the Studio server: performance, disks, network and the locations that hold state.
  await view.locator('[data-outline-id="machine:studio-server"]').click();
  const facts = view.getByTestId('structure-facts');
  await expect(facts).toContainText('Observed, not declared');
  await expect(facts).toContainText('12% of 12 cores');
  await expect(facts).toContainText('84.0 GiB used · 16.0 GiB free');
  await expect(facts).toContainText('Workspace repository');
  await expect(facts).toContainText('backup and git remote');
  await expect(facts).toContainText('Secrets key');
  await expect(facts).toContainText('not covered');

  for (const width of [1280, 390] as const) {
    await page.setViewportSize({ width, height: width === 1280 ? 900 : 844 });
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
      await expect(node('machine:studio-server')).toBeVisible();
      await page.screenshot({ path: path.join(results, `structure-observed-${width}-${theme}.png`), fullPage: true });
      // No box leaves the drawing area at this width.
      const overflow = await view.locator('.structure-canvas').evaluate(canvas =>
        [...canvas.querySelectorAll<HTMLElement>('.structure-box')].filter(box =>
          box.offsetLeft + box.offsetWidth > (canvas as HTMLElement).offsetWidth + 1).length);
      expect(overflow).toBe(0);
    }
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'light'; });
  await view.getByRole('button', { name: 'Load', exact: true }).click();
  await expect(node('machine:review-1')).toContainText('37% CPU');
  await view.locator('[data-outline-id="machine:review-1"]').click();
  await expect(facts).toContainText('1 busy · 1 free');
  await expect(facts).toContainText('AGT-10 refused (toolchain:playwright)');
  await view.screenshot({ path: path.join(results, 'structure-observed-host-facts.png') });

  // A changed structure reaches the open view without a reload.
  data = { ...observedStructure, revision: observedStructure.revision! + 1, nodes: observedStructure.nodes.map(item =>
    item.id === 'machine:review-1' ? { ...item, observation: { ...item.observation!, health: 'down', cpuPercent: 81 } } : item) };
  await expect(node('machine:review-1').locator('.structure-observation-state')).toContainText('Down', { timeout: 30_000 });
  await expect(node('machine:review-1')).toContainText('81% CPU');
  await expect(view).toHaveAttribute('data-revision', String(data.revision));
  expect(reads.some(read => read.status === 200)).toBe(true);
  expect(pageErrors).toEqual([]);
});

test('the running Studio shows its own server and pushes changes as structureDelta frames', async ({ page }) => {
  test.setTimeout(120_000);
  fs.mkdirSync(results, { recursive: true });
  const deltas: { scope: string; baseRevision: number; revision: number; nodes: { id: string }[] }[] = [];
  page.on('websocket', socket => socket.on('framereceived', frame => {
    for (const message of String(frame.payload).split('\u001e'))
      if (message.includes('"target":"structureDelta"'))
        deltas.push(JSON.parse(message).arguments[0]);
  }));
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'structure' }], activeKey: 'structure',
  })));
  const first = page.waitForResponse(response => /\/api\/v1\/operations\/structure(?:\?|$)/.test(response.url()) && response.status() === 200);
  await page.goto('/#/workspace/operations/structure?level=3');
  const body = await (await first).json() as { revision: number; schemaVersion: number; nodes: { id: string; provenance: string }[] };
  expect(body.schemaVersion).toBe(2);
  expect(body.revision).toBeGreaterThan(0);

  // Nothing is declared on this instance: the Studio server and its components show from what the API knows.
  const view = page.getByTestId('structure-view');
  const machine = body.nodes.find(node => node.id.startsWith('machine:') && node.provenance === 'observed')!;
  expect(machine).toBeTruthy();
  const name = machine.id.slice('machine:'.length);
  await expect(view.locator(`[data-node-id="${machine.id}"]`)).toBeVisible();
  for (const role of ['api', 'web', 'edge', 'engine'])
    await expect(view.locator(`[data-node-id="component:${name}:${role}"]`)).toBeVisible();
  await expect(view.locator(`[data-node-id="component:${name}:api"] .structure-observation-state`)).toContainText('Up');
  await expect(view).toHaveAttribute('data-live', 'true');

  // The API samples itself again within its interval; the change arrives as a small delta on the hub.
  await expect.poll(() => deltas.length, { timeout: 60_000 }).toBeGreaterThan(0);
  const delta = deltas[0];
  expect(delta.scope).toBe('workspace');
  expect(delta.revision).toBeGreaterThan(delta.baseRevision);
  expect(delta.nodes.length).toBeGreaterThan(0);
  expect(delta.nodes.length).toBeLessThan(body.nodes.length);
  expect(delta.nodes.every(node => node.id === machine.id || node.id.startsWith(`component:${name}:`))).toBe(true);
  await expect.poll(async () => Number(await view.getAttribute('data-revision')), { timeout: 30_000 })
    .toBeGreaterThanOrEqual(delta.revision);
  await page.screenshot({ path: path.join(results, 'structure-live-running-studio.png'), fullPage: true });
});
