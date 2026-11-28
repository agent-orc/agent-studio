import { test, expect, type Locator, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { liveSourcesFixture, liveStructureFixture, projectedCapacityFixture, sharedRunnerSourcesFixture, sharedRunnerStructureFixture } from '../../src/app/features/structure/live/live-structure.fixtures';

// AGT-4024: the live structure view in Operations, Structure. Mocked; role-named fixtures only.
const results = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');
const LAYERS = ['perf', 'network', 'storage', 'backup', 'warnings'] as const;
const API = 'component:studio-server:api';
const REVIEW_3 = 'machine:review-3';
const STUDIO = 'machine:studio-server';

/** What the mocked structure read answers; a test swaps it to let the data change under the view. */
interface Served { structure: typeof liveStructureFixture; runners?: typeof liveSourcesFixture.runners }

/** The installation one read later: a container more on the Studio server, which moves the machines around it. */
const grownStructure: typeof liveStructureFixture = { ...liveStructureFixture, revision: 42, nodes: [...liveStructureFixture.nodes,
  ...['cache', 'queue', 'mail'].map(name => ({ ...liveStructureFixture.nodes.find(node => node.id === 'component:studio-server:web')!,
    id: `component:studio-server:${name}`, label: name, unitId: name, detail: `stack-${name}-1` }))] };

async function mockApi(page: Page, sources: Partial<Record<'backups' | 'hosts' | 'environment', number>> = {},
  served: Served = { structure: liveStructureFixture }): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'structure' }], activeKey: 'structure',
  })));
  await page.context().route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.includes('/api/v1/operations/structure/library')) return json({ registries: [], consumers: [] });
    if (url.includes('/api/v1/operations/structure')) return json(served.structure);
    if (url.includes('/api/operator/backups')) return json(liveSourcesFixture.backup, sources.backups ?? 200);
    // The Studio shell reads the same list for its provider status, so each entry carries the full wire shape.
    if (url.includes('/api/v1/management/remote-hosts')) return json((served.runners ?? liveSourcesFixture.runners)!.map(runner => ({
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
async function box(view: Locator, id: string): Promise<{ x: number; y: number; w: number; h: number; level: number; facts: number }> {
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
  return { x, y, w, h, level: Number(await probe.getAttribute('data-level')), facts: Number(await probe.getAttribute('data-facts')) };
}

/** Where the camera rests: world point at the centre of the view and the scale. */
async function cameraView(view: Locator, around: string): Promise<string> {
  await box(view, around);
  return (await view.getByTestId('live-canvas').getAttribute('data-view'))!;
}

/**
 * Owner feedback 2: at the container level and closer no node in view is a bare box.
 * Every node whose box lies in the pane and that is at level 3 or 4 carries at least one fact
 * or one "not observed, because" sentence. Returns how many nodes were checked.
 */
async function expectNoBareBox(view: Locator, around: string, what: string): Promise<number> {
  await box(view, around);
  const stage = (await view.getByTestId('live-canvas').boundingBox())!;
  const probes = await view.locator('[data-testid="live-node"]').evaluateAll(nodes => nodes.map(node => ({
    id: node.getAttribute('data-node-id')!, level: Number(node.getAttribute('data-level')), facts: Number(node.getAttribute('data-facts')),
    box: node.getAttribute('data-box')!.split(',').map(Number) })));
  const near = probes.filter(probe => probe.level >= 3 && probe.box[0] >= 0 && probe.box[1] >= 0
    && probe.box[0] + probe.box[2] <= stage.width && probe.box[1] + probe.box[3] <= stage.height);
  for (const probe of near) expect(probe.facts, `${probe.id} is a bare box (${what})`).toBeGreaterThan(0);
  return near.length;
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
  await expect(view).toBeVisible({ timeout: 60_000 });
  await box(view, STUDIO);
  return view;
}

/** A screenshot with the pointer parked in a corner, so no tooltip or hover ring of the last action is in it. */
async function shot(page: Page, name: string): Promise<void> {
  await page.mouse.move(1, 1);
  await page.waitForTimeout(120);
  const filename = name.endsWith('--mocked') ? name : `${name}--mocked`;
  await page.screenshot({ path: path.join(results, `${filename}.png`) });
}

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
  await expect(view).not.toHaveAttribute('data-selected');
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

  // One click on an outline row selects the node and brings it into view at the container level.
  const row = view.locator(`[data-outline-id="${REVIEW_3}"]`);
  await row.click();
  await expect(view).toHaveAttribute('data-selected', REVIEW_3);
  await expect(row).toHaveAttribute('aria-current', 'true');
  await expect(details.getByTestId('live-details-name')).toHaveText('Review host 3');
  await expect(details.getByTestId('live-details-warnings')).toContainText('Missing toolchain:playwright');
  const stage = (await canvas.boundingBox())!;
  const revealed = await box(view, REVIEW_3);
  expect(revealed.level).toBe(3);
  expect(revealed.facts).toBeGreaterThan(3);
  expect(revealed.x).toBeGreaterThanOrEqual(0);
  expect(revealed.y).toBeGreaterThanOrEqual(0);
  expect(revealed.x + revealed.w).toBeLessThanOrEqual(stage.width + 1);
  expect(revealed.y + revealed.h).toBeLessThanOrEqual(stage.height + 1);
  await expect(view.getByTestId('live-crumbs')).toContainText('Review host 3');
  await expect(view.getByTestId('live-level')).toContainText('Detail level 3 of 4');
  await details.evaluate(element => element.parentElement?.scrollTo(0, 0));
  await page.waitForTimeout(150);
  await shot(page, 'live-structure-container-details');
  // The same row again: the closest level.
  await row.click();
  await expect.poll(async () => (await box(view, REVIEW_3)).level).toBe(4);
  await page.waitForTimeout(150);
  await shot(page, 'live-structure-container-closest');

  // Zoom with the keyboard and the wheel: the node gets more facts, never smaller type.
  await canvas.focus();
  await page.keyboard.press('0');
  await view.getByTestId('live-details-close').click();
  await expect(view).not.toHaveAttribute('data-selected');
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

test('live structure: jump to a container, go back, move like a map, and keep the place when layers or data change', async ({ page }) => {
  test.setTimeout(300_000);
  fs.mkdirSync(results, { recursive: true });
  const errors = watchErrors(page);
  const served: Served = { structure: liveStructureFixture };
  await mockApi(page, {}, served);
  await page.setViewportSize({ width: 1440, height: 900 });
  const view = await open(page);
  const canvas = view.getByTestId('live-canvas');
  const back = view.getByTestId('live-back');
  const crumbs = view.getByTestId('live-crumbs');
  const stage = (await canvas.boundingBox())!;
  const within = (node: { x: number; y: number; w: number; h: number }): void => {
    expect(node.x).toBeGreaterThanOrEqual(0);
    expect(node.y).toBeGreaterThanOrEqual(0);
    expect(node.x + node.w).toBeLessThanOrEqual(stage.width + 1);
    expect(node.y + node.h).toBeLessThanOrEqual(stage.height + 1);
  };

  // A way back is always in view: the breadcrumb, Fit, and Back, which has nowhere to go yet.
  await expect(crumbs).toBeVisible();
  await expect(view.getByTestId('live-zoom-fit')).toBeVisible();
  await expect(back).toBeVisible();
  await expect(back).toBeDisabled();
  const overview = await cameraView(view, STUDIO);
  expect((await box(view, REVIEW_3)).level).toBe(1);

  // Jump field: slash, a few letters, Enter. The container is selected and shown at the container level.
  await canvas.focus();
  await page.keyboard.press('/');
  const field = view.getByTestId('live-jump-field');
  await expect(field).toBeFocused();
  await field.pressSequentially('review 3');
  await expect(view.getByTestId('live-jump-option').first()).toContainText('Review host 3');
  await shot(page, 'live-structure-jump-field');
  await page.keyboard.press('Enter');
  await expect(view).toHaveAttribute('data-selected', REVIEW_3);
  await expect(canvas).toBeFocused();
  const jumped = await box(view, REVIEW_3);
  expect(jumped.level).toBe(3);
  within(jumped);
  await expect(crumbs).toContainText('Review host 3');
  await expect(view.getByTestId('live-level')).toContainText('Detail level 3 of 4 · Container');
  await expect(back).toBeEnabled();
  await page.waitForTimeout(150);
  await shot(page, 'live-structure-container-level');

  // At the container level nothing in view is a bare box, whatever layers are on.
  for (const on of [['perf', 'network', 'warnings'], [], [...LAYERS], ['backup'], ['storage', 'network']]) {
    await setLayers(view, on);
    expect(await expectNoBareBox(view, REVIEW_3, `layers ${on.join(',') || 'none'}`)).toBeGreaterThanOrEqual(2);
  }
  await setLayers(view, ['perf', 'network', 'warnings']);

  // Escape goes back to the view before the jump, exactly.
  await canvas.focus();
  await page.keyboard.press('Escape');
  expect(await cameraView(view, STUDIO)).toBe(overview);
  await expect(view).not.toHaveAttribute('data-selected');
  await expect(back).toBeDisabled();

  // Double click on a node: container level. Enter: the closest level. Enter again: container level.
  const far = await box(view, API);
  await page.mouse.dblclick(stage.x + far.x + far.w / 2, stage.y + far.y + far.h / 2);
  await expect(view).toHaveAttribute('data-selected', API);
  const near = await box(view, API);
  expect(near.level).toBe(3);
  within(near);
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await box(view, API)).level).toBe(4);
  expect(await expectNoBareBox(view, API, 'closest level')).toBeGreaterThanOrEqual(1);
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await box(view, API)).level).toBe(3);
  // The Back control returns step by step, then drops the selection.
  for (let step = 0; step < 3; step++) await back.click();
  expect(await cameraView(view, STUDIO)).toBe(overview);
  await expect(view).toHaveAttribute('data-selected', API);
  await back.click();
  await expect(view).not.toHaveAttribute('data-selected');
  await expect(back).toBeDisabled();

  // Move as on a map. A drag moves the plan by exactly what the pointer moved, with no glide after a slow release.
  await view.locator(`[data-outline-id="${API}"]`).click();
  const start = await box(view, API);
  const grab = { x: stage.x + start.x + start.w / 2, y: stage.y + start.y + start.h + 40 };
  await page.mouse.move(grab.x, grab.y);
  await page.mouse.down();
  await page.mouse.move(grab.x - 120, grab.y - 60, { steps: 8 });
  await page.waitForTimeout(200);
  await page.mouse.up();
  const dragged = await box(view, API);
  expect(Math.abs(dragged.x - (start.x - 120))).toBeLessThanOrEqual(2);
  expect(Math.abs(dragged.y - (start.y - 60))).toBeLessThanOrEqual(2);
  expect(dragged.w).toBe(start.w);
  await expect(view).toHaveAttribute('data-selected', API);
  // A quick release lets it glide on a little.
  await page.mouse.move(grab.x - 120, grab.y - 60);
  await page.mouse.down();
  // Deliver the final flick in one browser turn. A slow driver round trip can exceed the
  // 70 ms release window, turning a fast gesture into the slow release already tested above.
  await canvas.evaluate((element, start) => {
    for (const offset of [20, 40, 60])
      element.dispatchEvent(new PointerEvent('pointermove', { pointerId: 1, pointerType: 'mouse',
        buttons: 1, clientX: start.x + offset, clientY: start.y }));
    element.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1, pointerType: 'mouse',
      clientX: start.x + 60, clientY: start.y }));
  }, { x: grab.x - 120, y: grab.y - 60 });
  await page.mouse.up();
  expect((await box(view, API)).x).toBeGreaterThan(dragged.x + 62);

  // The wheel zooms towards the pointer: the spot under it stays under it.
  const before = await box(view, API);
  const spot = { x: before.x + before.w * 0.25, y: before.y + before.h * 0.25 };
  await page.mouse.move(stage.x + spot.x, stage.y + spot.y);
  await page.mouse.wheel(0, -100);
  await page.mouse.wheel(0, -100);
  const zoomed = await box(view, API);
  expect(zoomed.w).toBeGreaterThan(before.w * 1.4);
  expect(Math.abs(zoomed.x + zoomed.w * 0.25 - spot.x)).toBeLessThanOrEqual(3);
  expect(Math.abs(zoomed.y + zoomed.h * 0.25 - spot.y)).toBeLessThanOrEqual(3);
  await page.mouse.wheel(0, 100);
  await page.mouse.wheel(0, 100);
  expect(Math.abs((await box(view, API)).w - before.w)).toBeLessThanOrEqual(2);

  // Arrow keys move the plan in steps, Shift in larger ones; plus and minus zoom.
  await canvas.focus();
  const rest = await box(view, API);
  await page.keyboard.press('ArrowRight');
  expect(Math.abs((await box(view, API)).x - (rest.x - 90))).toBeLessThanOrEqual(2);
  await page.keyboard.press('ArrowDown');
  expect(Math.abs((await box(view, API)).y - (rest.y - 90))).toBeLessThanOrEqual(2);
  await page.keyboard.press('Shift+ArrowRight');
  expect(Math.abs((await box(view, API)).x - (rest.x - 360))).toBeLessThanOrEqual(2);
  await page.keyboard.press('Shift+ArrowLeft');
  await page.keyboard.press('ArrowLeft');
  await page.keyboard.press('ArrowUp');
  const home = await box(view, API);
  expect(Math.abs(home.x - rest.x) + Math.abs(home.y - rest.y)).toBeLessThanOrEqual(2);
  await page.keyboard.press('+');
  expect((await box(view, API)).w).toBeGreaterThan(rest.w * 1.4);
  await page.keyboard.press('-');
  expect(Math.abs((await box(view, API)).w - rest.w)).toBeLessThanOrEqual(2);

  // The place is kept. Switching layers does not move or zoom the plan.
  await view.locator(`[data-outline-id="${REVIEW_3}"]`).click();
  await box(view, REVIEW_3);
  const hold = (await canvas.boundingBox())!;
  await page.mouse.move(hold.x + 80, hold.y + hold.height - 120);
  await page.mouse.down();
  await page.mouse.move(hold.x + 115, hold.y + hold.height - 140, { steps: 5 });
  await page.waitForTimeout(200);
  await page.mouse.up();
  const place = await cameraView(view, REVIEW_3);
  const kept = await box(view, REVIEW_3);
  for (const layer of LAYERS) {
    await view.getByTestId(`live-layer-${layer}`).click();
    expect(await cameraView(view, REVIEW_3), `after switching ${layer}`).toBe(place);
  }
  await setLayers(view, ['perf', 'network', 'warnings']);
  expect(await cameraView(view, REVIEW_3)).toBe(place);

  // Neither does new data, even when it rearranges the plan: three containers appear on the
  // Studio server, its frame grows, and the machine the view rests on is pushed aside.
  const frame = await box(view, STUDIO);
  served.structure = grownStructure;
  await expect(view).toHaveAttribute('data-revision', '42', { timeout: 45_000 });
  await expect(view.getByTestId('live-outline')).toContainText('Cache');
  const grown = await box(view, STUDIO);
  expect(grown.h).toBeGreaterThan(frame.h + 20);
  const after = await box(view, REVIEW_3);
  expect([after.x, after.y, after.w, after.h]).toEqual([kept.x, kept.y, kept.w, kept.h]);
  expect((await cameraView(view, REVIEW_3)).split(',')[2]).toBe(place.split(',')[2]);
  await expect(view).toHaveAttribute('data-selected', REVIEW_3);

  // Fit shows everything, and Back returns to the container.
  await view.getByTestId('live-zoom-fit').click();
  expect((await box(view, REVIEW_3)).level).toBe(1);
  await back.click();
  const again = await box(view, REVIEW_3);
  expect([again.x, again.y, again.w]).toEqual([kept.x, kept.y, kept.w]);
  expect(errors).toEqual([]);
});

test('live structure: every zoom level adds facts to the node, following the active layers', async ({ page }) => {
  test.setTimeout(180_000);
  fs.mkdirSync(results, { recursive: true });
  const errors = watchErrors(page);
  await mockApi(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  const view = await open(page);
  const canvas = view.getByTestId('live-canvas');
  const stage = (await canvas.boundingBox())!;
  const REVIEW_1 = 'machine:review-1';
  // Zoom in on one container with the wheel, level by level: the count of facts on it only grows.
  const counts: number[] = [];
  let node = await box(view, REVIEW_1);
  expect(node.level).toBe(1);
  await shot(page, 'live-structure-level-1');
  counts.push(node.facts);
  for (let level = 2; level <= 4; level++) {
    for (let step = 0; step < 14 && node.level < level; step++) {
      await page.mouse.move(stage.x + node.x + node.w / 2, stage.y + node.y + node.h / 2);
      await page.mouse.wheel(0, -100);
      node = await box(view, REVIEW_1);
    }
    expect(node.level).toBe(level);
    await expect(view.getByTestId('live-level')).toContainText(`Detail level ${level} of 4`);
    // One more notch inside the level, so the shot shows the level with room, not at its threshold.
    if (level < 4) {
      await page.mouse.move(stage.x + node.x + node.w / 2, stage.y + node.y + node.h / 2);
      await page.mouse.wheel(0, -100);
      node = await box(view, REVIEW_1);
      expect(node.level).toBe(level);
    }
    counts.push(node.facts);
    await page.waitForTimeout(150);
    await shot(page, `live-structure-level-${level}`);
  }
  expect(counts[0]).toBe(0);
  for (let i = 1; i < counts.length; i++) expect(counts[i], `level ${i + 1} shows more than level ${i}`).toBeGreaterThan(counts[i - 1]);

  // The backup layer on: the storage strip adds its backup facts, and loses them again with the layer off.
  const STORAGE = `${STUDIO}#storage`;
  await setLayers(view, []);
  await view.locator(`[data-outline-id="${STORAGE}"]`).click();
  const plain = await box(view, STORAGE);
  expect(plain.level).toBe(3);
  await setLayers(view, ['backup']);
  const withBackup = await box(view, STORAGE);
  expect([withBackup.x, withBackup.y, withBackup.w]).toEqual([plain.x, plain.y, plain.w]);
  expect(withBackup.facts).toBeGreaterThan(plain.facts);
  await page.waitForTimeout(150);
  await shot(page, 'live-structure-storage-backup-layer');
  expect(errors).toEqual([]);
});

test('live structure: reads that fail show as not observed, and the classic drawing stays one switch away', async ({ page }) => {
  test.setTimeout(180_000);
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
  await expect(page.getByTestId('live-structure')).toHaveAttribute('data-layers', 'backup', { timeout: 60_000 });
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
    // An outline row brings the container into view at the container level and folds the sheet away from the plan.
    await view.getByRole('button', { name: 'Outline' }).click();
    await view.locator(`[data-outline-id="${REVIEW_3}"]`).click();
    await expect(view).toHaveAttribute('data-selected', REVIEW_3);
    await expect(view.getByTestId('live-panel')).toHaveAttribute('data-sheet', 'peek');
    await expect(view.getByTestId('live-sheet-handle')).toContainText('Review host 3');
    const near = await box(view, REVIEW_3);
    expect(near.level).toBe(3);
    expect(near.x).toBeGreaterThanOrEqual(0);
    expect(near.x + near.w).toBeLessThanOrEqual(390);
    expect(await expectNoBareBox(view, REVIEW_3, 'phone')).toBeGreaterThanOrEqual(2);
    await page.waitForTimeout(150);
    await shot(page, 'live-structure-phone-container');
    // The way back and the jump field are in reach of a thumb.
    for (const id of ['live-back', 'live-zoom-fit', 'live-jump-field']) {
      const target = (await view.getByTestId(id).boundingBox())!;
      expect(target.height, id).toBeGreaterThanOrEqual(44);
    }
    await view.getByTestId('live-jump-field').tap();
    await view.getByTestId('live-jump-field').pressSequentially('gate host');
    await expect(view.getByTestId('live-jump-option').first()).toContainText('Gate host');
    await view.getByTestId('live-jump-option').first().tap();
    await expect(view).toHaveAttribute('data-selected', 'machine:gate-1');
    expect((await box(view, 'machine:gate-1')).level).toBe(3);
    await view.getByTestId('live-back').tap();
    await expect(view).toHaveAttribute('data-selected', REVIEW_3);
    expect((await box(view, REVIEW_3)).x).toBe(near.x);
    // One finger moves the plan by what it moved; two fingers pinch towards their middle.
    const client = await page.context().newCDPSession(page);
    const touch = (type: 'touchStart' | 'touchMove' | 'touchEnd', points: { x: number; y: number }[]) =>
      client.send('Input.dispatchTouchEvent', { type, touchPoints: points.map((point, id) => ({ x: Math.round(point.x), y: Math.round(point.y), id })) });
    const pane = (await view.getByTestId('live-canvas').boundingBox())!;
    const finger = { x: pane.x + near.x + near.w / 2, y: pane.y + near.y + near.h / 2 };
    await touch('touchStart', [finger]);
    for (let step = 1; step <= 5; step++) await touch('touchMove', [{ x: finger.x + step * 8, y: finger.y + step * 6 }]);
    await page.waitForTimeout(200);
    await touch('touchEnd', []);
    const moved = await box(view, REVIEW_3);
    expect(Math.abs(moved.x - (near.x + 40))).toBeLessThanOrEqual(2);
    expect(Math.abs(moved.y - (near.y + 30))).toBeLessThanOrEqual(2);
    const middle = { x: pane.x + moved.x + moved.w / 2, y: pane.y + moved.y + moved.h / 2 };
    await touch('touchStart', [{ x: middle.x - 40, y: middle.y }, { x: middle.x + 40, y: middle.y }]);
    for (let step = 1; step <= 6; step++) await touch('touchMove', [{ x: middle.x - 40 - step * 5, y: middle.y }, { x: middle.x + 40 + step * 5, y: middle.y }]);
    await touch('touchEnd', []);
    const pinched = await box(view, REVIEW_3);
    expect(pinched.w / moved.w).toBeGreaterThan(1.6);
    expect(pinched.w / moved.w).toBeLessThan(1.9);
    expect(Math.abs(pinched.x + pinched.w / 2 - (moved.x + moved.w / 2))).toBeLessThanOrEqual(4);
    expect(Math.abs(pinched.y + pinched.h / 2 - (moved.y + moved.h / 2))).toBeLessThanOrEqual(4);
    await expect(view).toHaveAttribute('data-selected', REVIEW_3);
    await view.getByTestId('live-sheet-handle').tap();
    await expect(view.getByTestId('live-details-name')).toHaveText('Review host 3');
    expect(await overflow()).toBeLessThanOrEqual(0);
    expect(errors).toEqual([]);
  });
});

// Uses facts emitted by ObservedStructureProjection, including capacity on the role children.
test('live structure: unique capacity agrees across layers, installation details and process facts', async ({ page }) => {
  test.setTimeout(120_000);
  fs.mkdirSync(results, { recursive: true });
  const errors = watchErrors(page);
  await mockApi(page, {}, { structure: projectedCapacityFixture, sharedRunnerSourcesFixture, sharedRunnerStructureFixture });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/#/workspace/operations/structure');
  const view = page.getByTestId('live-structure');
  await expect(view).toBeVisible({ timeout: 30_000 });
  await box(view, 'machine:shared-host');
  await expect(view.getByTestId('live-layer-fact-perf')).toHaveText('5 of 10 slots busy');
  const details = view.getByTestId('live-details');
  await expect(details.getByTestId('live-details-kpis').getByText('Slots in use', { exact: true }).locator('..')).toContainText('5 of 10');
  await expect(details.getByTestId('live-details-kpis').getByText('Queued', { exact: true }).locator('..')).toContainText('not observed');
  await expect(details.locator('[data-fact="slots"]')).toContainText('5 of 10');
  await expect(details.locator('[data-fact="reporting"]')).toContainText('2');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await shot(page, `live-structure-unique-capacity-${theme}`);
  }
  for (const [id, slots, cards] of [
    ['machine:shared-host', '3 of 6 in use', 'AGT-3, AGT-4, AGT-5'],
    ['component:shared-host:shared-coding', '2 of 4 in use', 'AGT-3, AGT-4'],
    ['component:shared-host:shared-review', '1 of 2 in use', 'AGT-5'],
    ['component:coding-host:coding-runner', '2 of 4 in use', 'AGT-1, AGT-2'],
  ]) {
    await view.locator(`[data-outline-id="${id}"]`).click();
    await expect(details).toHaveAttribute('data-node-id', id);
    await expect(details.locator('[data-fact="slots"]')).toContainText(slots);
    await expect(details.locator('[data-fact="job"]')).toContainText(cards);
    await expect(view.getByTestId('live-layer-fact-perf')).toHaveText('5 of 10 slots busy');
  }
  expect(errors).toEqual([]);
});

for (const reverse of [false, true]) {
  test(`live structure: shared host keeps runner facts by identity (reversed: ${reverse})`, async ({ page }) => {
    test.setTimeout(120_000);
    fs.mkdirSync(results, { recursive: true });
    const errors = watchErrors(page);
    const runners = [...sharedRunnerSourcesFixture.runners!];
    await mockApi(page, {}, { structure: sharedRunnerStructureFixture, runners: reverse ? runners.reverse() : runners });
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/#/workspace/operations/structure');
    const view = page.getByTestId('live-structure');
    await expect(view).toBeVisible({ timeout: 30_000 });
    await box(view, 'machine:shared-host');
    const details = view.getByTestId('live-details');
    for (const [role, version, restarts, uptime] of [['coding', '2.1', 0, '4 h'], ['review', '2.2', 2, '60 min']] as const) {
      const id = `component:shared-host:shared-${role}`;
      await view.locator(`[data-outline-id="${id}"]`).click();
      await expect(details).toHaveAttribute('data-node-id', id);
      await expect(details.locator('[data-fact="image"]')).toContainText(`registry.example/studio/${role}-host:${version}`);
      await expect(details.locator('[data-fact="restarts"]')).toContainText(`${restarts} of the review daemon`);
      await expect(details.locator('[data-fact="uptime"]')).toContainText(uptime);
      await expect(details.locator('[data-fact^="event:"]')).toHaveCount(restarts);
      if (role === 'review') await expect(details.locator('[data-fact="event:0"]')).toContainText('3 reviews lost');
      if (!reverse) await shot(page, `live-structure-${role}-identity--mocked`);
    }
    await view.locator('[data-outline-id="machine:shared-host"]').click();
    await expect(details).toHaveAttribute('data-node-id', 'machine:shared-host');
    await expect(details.locator('[data-fact="runners"]')).toContainText('coding (shared-coding) · review (shared-review)');
    await expect(details.locator('[data-fact="image"]')).toHaveCount(0);
    for (const [role, version, count] of [['coding', '2.1', 0], ['review', '2.2', 2]] as const) {
      await expect(details.locator(`[data-fact="runner:shared-${role}:image"]`)).toContainText(`${role}-host:${version}`);
      await expect(details.locator(`[data-fact="runner:shared-${role}:restarts"]`)).toContainText(`${count} of the review daemon`);
    }
    if (!reverse) for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await shot(page, `live-structure-shared-runner-host-${theme}--mocked`);
    }
    expect(errors).toEqual([]);
  });
}

test('recorded two-machine installation, capacity and layer views', async ({ page }) => {
  test.setTimeout(240_000);
  const structure = JSON.parse(fs.readFileSync(path.resolve('..', 'testdata/structure/workspace.json'), 'utf8'));
  await mockApi(page, {}, { structure, runners: [] });
  await page.setViewportSize({ width: 1600, height: 1000 });
  const view = await open(page);
  await expect(view.getByTestId('live-node')).toHaveCount(30);
  await expect(view.getByTestId('live-details-network').locator('[data-link]')).toHaveCount(11);
  await expect(view.getByTestId('installation-summary')).toHaveText('0 open warnings, 16 slots in use (limit partially observed), queue not observed');
  const outline = view.getByTestId('live-outline');
  for (const label of ['Debian VM', 'Owner workstation', 'Review runner 3', 'Post-merge tests', 'Studio stack'])
    await expect(outline).toContainText(label);
  for (const width of [1280, 1600]) {
    await page.setViewportSize({ width, height: 1000 });
    await box(view, STUDIO);
    const overlap = await view.getByTestId('live-node').evaluateAll((elements, nodes: { id: string; parentId: string | null }[]) => {
      const rectangles = new Map(elements.map(element => [element.getAttribute('data-node-id'),
        element.getAttribute('data-box')!.split(',').map(Number)]));
      return nodes.flatMap((a, index) => nodes.slice(index + 1).filter(b => {
        if (a.parentId !== b.parentId || !rectangles.has(a.id) || !rectangles.has(b.id)) return false;
        const [x, y, w, h] = rectangles.get(a.id)!, [u, v, p, q] = rectangles.get(b.id)!;
        return x < u+p && u < x+w && y < v+q && v < y+h;
      }).map(b => [a.id, b.id]));
    }, structure.nodes);
    expect(overlap).toEqual([]);
  }
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await shot(page, 'recorded-01-' + theme);
  }
  await setTheme(page, 'light');
  await view.locator(`[data-outline-id="${STUDIO}"]`).click();
  await box(view, STUDIO);
  await shot(page, 'recorded-03-studio');
  const coding = 'component:studio-agents:coding-runner';
  await view.locator(`[data-outline-id="${coding}"]`).click();
  await box(view, coding);
  await expect(view.locator('[data-slot]')).toHaveCount(18);
  await expect(view.getByRole('button', { name: 'AGT-4018', exact: true })).toBeVisible();
  await shot(page, 'recorded-04-coding');
  await view.getByTestId('live-zoom-fit').click();
  await view.getByTestId('live-details-close').click();
  for (const [index, layer] of LAYERS.entries()) {
    await setLayers(view, [layer]);
    await box(view, STUDIO);
    await shot(page, `recorded-${String(index + 6).padStart(2, '0')}-${layer}`);
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await setLayers(view, ['perf', 'network', 'warnings']);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await box(view, STUDIO);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    await shot(page, 'recorded-phone-' + theme);
  }
});
