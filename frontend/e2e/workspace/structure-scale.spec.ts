import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { structureFixtures } from '../../src/app/features/structure/structure.fixtures';

// AGT-3349 S5: complex setups and scale. Role-named fixtures only.
const results = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');
type FixtureName = keyof typeof structureFixtures;

async function mockShell(page: Page, fixture: () => FixtureName): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'structure' }], activeKey: 'structure',
  })));
  await page.context().route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.includes('/api/v1/operations/structure')) return json(structureFixtures[fixture()]);
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

/** Geometry check on the rendered drawing: link path segments against instance and collapsed boxes. */
function renderedCrossings(page: Page) {
  return page.evaluate(() => {
    const canvas = document.querySelector<HTMLElement>('[data-testid="structure-canvas"]')!;
    const boxes = [...canvas.querySelectorAll<HTMLElement>('.structure-box[data-opaque="true"]')].map(element => ({
      id: element.querySelector('[data-node-id]')!.getAttribute('data-node-id')!,
      x: element.offsetLeft, y: element.offsetTop, w: element.offsetWidth, h: element.offsetHeight,
    }));
    const links = [...canvas.querySelectorAll<SVGPathElement>('path.structure-link[data-link-number]')].map(element => ({
      number: Number(element.dataset['linkNumber']),
      points: element.getAttribute('d')!.slice(1).split(' L').map(pair => pair.split(' ').map(Number)),
    }));
    const crossings: { link: number; box: string }[] = [];
    for (const link of links)
      for (let k = 1; k < link.points.length; k++) {
        const [p, q] = [link.points[k - 1], link.points[k]];
        for (const box of boxes)
          if (Math.max(p[0], q[0]) > box.x + 1 && Math.min(p[0], q[0]) < box.x + box.w - 1 &&
              Math.max(p[1], q[1]) > box.y + 1 && Math.min(p[1], q[1]) < box.y + box.h - 1)
            crossings.push({ link: link.number, box: box.id });
      }
    return { crossings, links: links.length, boxes: boxes.length,
      layoutMs: Number(canvas.dataset['layoutMs']), width: canvas.offsetWidth, height: canvas.offsetHeight };
  });
}

/** A hash-only navigation would keep the loaded declarations; reload so each fixture is fetched. */
async function open(page: Page, name: FixtureName): Promise<void> {
  await page.goto('/#/workspace/operations/structure');
  await page.reload();
  const first = structureFixtures[name].nodes.find(node => node.kind === 'boundary')!;
  await expect(page.locator(`[data-node-id="${first.id}"]`)).toContainText(first.label);
}

async function show(page: Page, view: 'host' | 'container', level: 1 | 2): Promise<void> {
  const structure = page.getByTestId('structure-view');
  await structure.getByRole('button', { name: view === 'host' ? 'Host' : 'Container', exact: true }).click();
  await page.keyboard.press(String(level));
  const canvas = structure.getByTestId('structure-canvas');
  await expect(canvas).toHaveAttribute('data-view', view);
  await expect(canvas).toHaveAttribute('data-level', String(level));
}

test('complex, AGT and workspace fixtures render without links across instance boxes', async ({ page }) => {
  fs.mkdirSync(results, { recursive: true });
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  let current: FixtureName = 'complex';
  await mockShell(page, () => current);
  await page.setViewportSize({ width: 1440, height: 900 });
  const metrics: Record<string, unknown> = {};
  for (const name of ['complex', 'agt', 'workspace'] as const) {
    current = name;
    await open(page, name);
    const view = page.getByTestId('structure-view');
    await expect(view.locator('[data-node-id]').first()).toBeVisible();
    for (const projection of ['host', 'container'] as const)
      for (const level of [1, 2] as const) {
        await show(page, projection, level);
        await expect(view.locator('path.structure-link[data-link-number]').first()).toBeAttached();
        const measured = await renderedCrossings(page);
        metrics[`${name}-${projection}-level${level}`] = measured;
        expect(measured.crossings, `${name} ${projection} level ${level}`).toEqual([]);
        expect(measured.links).toBeGreaterThan(0);
      }
  }

  // Level 1 of the 60-service, two-region fixture fits one 1440 px screen.
  current = 'complex';
  await open(page, 'complex');
  const view = page.getByTestId('structure-view');
  await show(page, 'host', 1);
  const canvas = view.getByTestId('structure-canvas');
  const fit = await page.evaluate(() => {
    const main = document.querySelector<HTMLElement>('.structure-main')!;
    const drawing = document.querySelector<HTMLElement>('[data-testid="structure-canvas"]')!.getBoundingClientRect();
    return { scrollWidth: main.scrollWidth, clientWidth: main.clientWidth, right: drawing.right, bottom: drawing.bottom,
      viewportWidth: window.innerWidth, viewportHeight: window.innerHeight };
  });
  metrics['complex-level1-fit'] = fit;
  expect(fit.scrollWidth).toBeLessThanOrEqual(fit.clientWidth);
  expect(fit.right).toBeLessThanOrEqual(1440);
  expect(fit.bottom).toBeLessThanOrEqual(900);
  // The first layout after load runs before the JIT warms up; the budget applies to the warm median.
  metrics['complex-level1-first-layout-ms'] = Number(await canvas.getAttribute('data-layout-ms'));
  const samples: number[] = [];
  for (let run = 0; run < 5; run++) {
    await show(page, 'host', 2);
    await show(page, 'host', 1);
    samples.push(Number(await canvas.getAttribute('data-layout-ms')));
  }
  samples.sort((p, q) => p - q);
  metrics['complex-level1-layout-ms-samples'] = samples;
  expect(samples[2]).toBeLessThan(50);

  // Managed services: dashed, worded by their declaration.
  const database = view.locator('.structure-box[data-kind="managed"]').filter({ has: page.locator('[data-node-id="eu-db"]') });
  const queue = view.locator('.structure-box[data-kind="managed"]').filter({ has: page.locator('[data-node-id="eu-mq"]') });
  await expect(database).toContainText('declared · probed via TLS');
  await expect(queue).toContainText('declared · not observed');
  expect(await database.evaluate(element => getComputedStyle(element).borderTopStyle)).toBe('dashed');
  expect(await queue.evaluate(element => getComputedStyle(element).borderTopStyle)).toBe('dashed');
  await expect(view.locator('[data-node-id="eu-db"]')).toHaveAttribute('aria-label', /declared, probed via TLS, Healthy/);
  await expect(view.locator('[data-node-id="eu-mq"]')).toHaveAttribute('aria-label', /declared, not observed/);

  // Every drawn link has one numbered key entry.
  const drawn = await view.locator('path.structure-link[data-link-number]').count();
  await expect(view.locator('[data-link-key]')).toHaveCount(drawn);

  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await show(page, 'host', 1);
    await view.screenshot({ path: path.join(results, `structure-complex-host-level1-${theme}.png`) });
    await show(page, 'host', 2);
    await view.screenshot({ path: path.join(results, `structure-complex-host-level2-${theme}.png`) });
    await show(page, 'container', 2);
    await view.screenshot({ path: path.join(results, `structure-complex-container-level2-${theme}.png`) });
  }
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'light'; });
  for (const name of ['agt', 'workspace'] as const) {
    current = name;
    await open(page, name);
    await show(page, 'host', 2);
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
      await view.screenshot({ path: path.join(results, `structure-${name}-host-level2-${theme}.png`) });
    }
  }
  fs.writeFileSync(path.join(results, 'structure-scale-metrics.json'), JSON.stringify(metrics, null, 2));
  expect(errors).toEqual([]);
});
