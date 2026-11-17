import { expect, test } from '@playwright/test';
import type { APIRequestContext, Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { setTheme, type Theme } from '../helpers/theme';

// AGT-3654: decide DEMO-W2 end to end against the isolated demo stack
// (`npm run e2e` with the demo profile): a real backend, the seeded demo
// workspace, and the host's git config. No route is mocked.
const PROJECT = 'Demo App';
const WORKBENCH_ID = 'export-retention';
const ANSWERED_OPTIONS = ['window-30', 'proof-receipt'];
const RESULTS = resolve(process.env['JOB_RESULTS_DIR']
  ?? resolve(__dirname, '..', '..', '..', 'results', 'AGT-3654'));

interface WorkbenchRead {
  workbench: {
    status: string;
    relatedTaskKeys?: string[];
    decision?: { spawnedTaskKeys?: string[] } | null;
  };
}

interface BoardCard { key?: string | null; title?: string | null }

test.use({ reducedMotion: 'reduce', video: 'off', trace: 'off' });
test.setTimeout(180_000);

test.beforeEach(() => {
  test.skip(process.env['PW_PREBUILT'] !== '1' || process.env['PW_E2E_PROFILE'] !== 'demo',
    'Runs on the isolated demo stack: npm run e2e with PW_E2E_PROFILE=demo.');
  mkdirSync(RESULTS, { recursive: true });
});

test('a Dossier decision that cannot be saved removes its card, and the retry decides the Dossier', async ({ page, request }) => {
  const repository = resolve(process.env['PW_E2E_WORKSPACE']!, 'projects', 'demo-app');
  commitSeededDocs(repository);
  await expect.poll(async () => (await readWorkbench(request)).workbench.decision ?? null).toBeNull();
  const lock = branchLock(repository);
  await page.setViewportSize({ width: 1280, height: 900 });
  await openDossier(page);
  await answerBothDecisions(page);
  const inline = page.getByTestId('workbench-inline-decision-action');
  await inline.getByTestId('workbench-decision-start').click();
  const title = `Implement export retention ${Date.now().toString(36)}`;
  await inline.getByTestId('workbench-decision-title').fill(title);

  // The branch ref is held, so the decision commit fails after the card was created.
  writeFileSync(lock, '');
  try {
    await inline.getByTestId('workbench-decision-confirm').click();
    const alert = inline.getByRole('alert');
    await expect(alert).toContainText('The Dossier decision could not be saved', { timeout: 30_000 });
    await expect(alert).toContainText('was removed again');
    await expect(alert).not.toContainText('git config');
    await expect.poll(() => boardTitles(request)).not.toContain(title);
    expect((await readWorkbench(request)).workbench.decision ?? null).toBeNull();
    await capture(page, 'dossier-decision-failed');
  } finally {
    rmSync(lock, { force: true });
  }

  // The retry creates a fresh card and records the decision with it.
  await page.reload();
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });
  await restoreAnswers(page);
  await revealAction(page);
  const confirm = inline.getByTestId('workbench-decision-confirm');
  if (await confirm.isVisible()) await inline.getByRole('button', { name: 'Cancel' }).click();
  await expect(inline).toContainText('2 of 2 answered');
  await inline.getByTestId('workbench-decision-start').click();
  await inline.getByTestId('workbench-decision-title').fill(title);
  await expect(confirm).toBeEnabled({ timeout: 30_000 });
  await confirm.click();
  const created = inline.getByTestId('workbench-decision-created-tasks');
  await expect(created).toContainText(title, { timeout: 30_000 });
  await expect(page.getByTestId('workbench-viewer-status')).toHaveAttribute('data-status', 'decided');
  const read = await readWorkbench(request);
  expect(read.workbench.status).toBe('decided');
  const spawned = read.workbench.decision?.spawnedTaskKeys ?? [];
  expect(spawned).toHaveLength(1);
  expect(read.workbench.relatedTaskKeys ?? []).toContain(spawned[0]);
  await expect(created).toContainText(spawned[0]);
  expect((await boardTitles(request)).filter(entry => entry === title)).toHaveLength(1);
  expect(execFileSync('git', ['-C', repository, 'log', '-1', '--format=%an <%ae>'], { encoding: 'utf8' }).trim())
    .toBe('Agent Studio <studio@agent-studio.invalid>');
  await capture(page, 'dossier-decision-decided');
});

test('one answer is durable and open; the later answer starts one card', async ({ page, request }) => {
  const repository = resolve(process.env['PW_E2E_WORKSPACE']!, 'projects', 'demo-app');
  commitSeededDocs(repository);
  await expect.poll(async () => (await readWorkbench(request)).workbench.decision ?? null).toBeNull();
  await page.setViewportSize({ width: 1280, height: 900 });
  await openDossier(page);
  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  await frame.locator('[data-option-id="window-30"] input').check();
  await revealAction(page);
  const inline = page.getByTestId('workbench-inline-decision-action');
  await expect(inline.getByTestId('workbench-decision-start')).toBeDisabled();
  await expect(inline).toContainText('1 of 2 answered');
  await inline.getByTestId('workbench-decision-record').click();
  await expect(inline).toContainText('Saved in Dossier', { timeout: 30_000 });
  await expect(inline).toContainText('1 still open');
  const partial = await readWorkbench(request);
  expect(partial.workbench.status).toBe('decision-pending');
  expect(partial.workbench.decision ?? null).toBeNull();
  await capture(page, 'dossier-one-answer-open');

  await frame.locator('[data-option-id="proof-receipt"] input').check();
  await revealAction(page);
  await inline.getByTestId('workbench-decision-start').click();
  const title = `Implement both export policies ${Date.now().toString(36)}`;
  await inline.getByTestId('workbench-decision-title').fill(title);
  await inline.getByTestId('workbench-decision-confirm').click();
  await expect(inline.getByTestId('workbench-decision-created-tasks')).toContainText(title, { timeout: 30_000 });
  const final = await readWorkbench(request);
  expect(final.workbench.decision?.spawnedTaskKeys).toHaveLength(1);
  expect((await boardTitles(request)).filter(entry => entry === title)).toHaveLength(1);
  await capture(page, 'dossier-both-answered');
});

// AGT-3832: on a phone the option text is the target, each point is a named
// group, the text wraps as one block, and the header keeps every control on-screen.
const PHONE_POINTS = [
  { option: 'window-30', question: 'Which retention window becomes the durable default?' },
  { option: 'proof-receipt', question: 'Do we owe a customer proof that an expired export was deleted?' },
];

test.describe('on a phone', () => {
  test.use({ hasTouch: true });

  for (const theme of ['light', 'dark'] as Theme[]) {
    test(`tapping option text answers both DEMO-W2 points and confirms (${theme})`, async ({ page, request }) => {
      const repository = resolve(process.env['PW_E2E_WORKSPACE']!, 'projects', 'demo-app');
      commitSeededDocs(repository);
      await expect.poll(async () => (await readWorkbench(request)).workbench.decision ?? null).toBeNull();
      await page.setViewportSize({ width: 390, height: 844 });
      await page.emulateMedia({ colorScheme: theme });
      await page.addInitScript(value => localStorage.setItem('atp.studio.theme', value), theme);
      await openDossier(page);
      await expectPhoneHeaderOnScreen(page, theme);

      const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
      for (const point of PHONE_POINTS) {
        const group = frame.getByRole('radiogroup', { name: point.question });
        await expect(group).toBeVisible({ timeout: 30_000 });
        const option = group.locator(`[data-option-id="${point.option}"]`);
        const label = option.locator('label');
        const text = option.locator('[data-studio-decision-text]');
        await text.scrollIntoViewIfNeeded();
        const target = await label.boundingBox();
        expect(target!.width).toBeGreaterThanOrEqual(24);
        expect(target!.height).toBeGreaterThanOrEqual(24);
        // One wrapping text block: the bold label starts the line its description continues.
        const layout = await text.evaluate(element => {
          const bold = element.querySelector('b')!.getBoundingClientRect();
          const block = element.getBoundingClientRect();
          return { boldTop: bold.top, blockTop: block.top, blockWidth: block.width,
            rowWidth: element.parentElement!.getBoundingClientRect().width };
        });
        expect(Math.abs(layout.boldTop - layout.blockTop)).toBeLessThan(4);
        expect(layout.blockWidth).toBeGreaterThan(layout.rowWidth * 0.7);
        await text.tap();
        await expect(option.locator('input')).toBeChecked();
        await expect(group.getByRole('radio', { checked: true })).toHaveCount(1);
      }
      await page.screenshot({ path: resolve(RESULTS, `dossier-phone-answered-${theme}-390.png`) });

      await revealAction(page);
      const inline = page.getByTestId('workbench-inline-decision-action');
      await expect(inline).toContainText('2 of 2 answered');
      await inline.getByTestId('workbench-decision-start').tap();
      const title = `Implement export retention on a phone ${theme} ${Date.now().toString(36)}`;
      await inline.getByTestId('workbench-decision-title').fill(title);
      const shell = await page.getByTestId('workbench-viewer-frame-shell').boundingBox();
      const frameBox = await page.getByTestId('workbench-viewer-frame').boundingBox();
      expect(frameBox!.height).toBeGreaterThan(shell!.height * 0.5);
      await page.screenshot({ path: resolve(RESULTS, `dossier-phone-confirm-${theme}-390.png`) });
      await inline.getByTestId('workbench-decision-confirm').tap();
      await expect(inline.getByTestId('workbench-decision-created-tasks')).toContainText(title, { timeout: 30_000 });
      expect((await readWorkbench(request)).workbench.status).toBe('decided');
      await page.screenshot({ path: resolve(RESULTS, `dossier-phone-decided-${theme}-390.png`) });
    });
  }
});

/** The key and title do not overlap, and every header control and the details close stay on-screen. */
async function expectPhoneHeaderOnScreen(page: Page, theme: Theme): Promise<void> {
  const header = page.getByTestId('workbench-viewer-header');
  await expect(page.getByTestId('workbench-viewer-title')).toBeVisible({ timeout: 30_000 });
  const geometry = await header.evaluate(element => {
    const box = (target: Element | null) => {
      if (!target) return null;
      const { left, right, top, bottom } = target.getBoundingClientRect();
      return { left, right, top, bottom };
    };
    return {
      key: box(element.querySelector('[data-testid="workbench-viewer-key"]')),
      title: box(element.querySelector('[data-testid="workbench-viewer-title"]')),
      children: Array.from(element.children)
        .filter(child => child.getBoundingClientRect().width > 0)
        .map(child => ({ name: child.tagName.toLowerCase() + '.' + child.className, ...box(child)! })),
    };
  });
  const viewport = page.viewportSize()!.width;
  for (const child of geometry.children) {
    expect(child.left, child.name).toBeGreaterThanOrEqual(0);
    expect(child.right, child.name).toBeLessThanOrEqual(viewport);
  }
  if (geometry.key) {
    const overlaps = geometry.key.right > geometry.title!.left && geometry.key.left < geometry.title!.right
      && geometry.key.bottom > geometry.title!.top && geometry.key.top < geometry.title!.bottom;
    expect(overlaps).toBe(false);
  }
  await page.getByTestId('workbench-viewer-details-trigger').tap();
  const close = page.getByTestId('workbench-viewer-details-popover').getByRole('button', { name: 'Close details' });
  await expect(close).toBeInViewport({ ratio: 1 });
  const popover = await page.getByTestId('workbench-viewer-details-popover').boundingBox();
  expect(popover!.x).toBeGreaterThanOrEqual(0);
  expect(popover!.x + popover!.width).toBeLessThanOrEqual(viewport);
  await page.screenshot({ path: resolve(RESULTS, `dossier-phone-details-${theme}-390.png`) });
  await close.tap();
  await page.screenshot({ path: resolve(RESULTS, `dossier-phone-header-${theme}-390.png`) });
}

// AGT-3829: decide DEMO-W2 with the keyboard only. Focus must never drop to the
// body, and no Tab press may leave it on the control it started from.
test('the keyboard reaches, opens, confirms, and leaves the Dossier decision', async ({ page, request }) => {
  const repository = resolve(process.env['PW_E2E_WORKSPACE']!, 'projects', 'demo-app');
  commitSeededDocs(repository);
  await expect.poll(async () => (await readWorkbench(request)).workbench.decision ?? null).toBeNull();
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.addInitScript(() => {
    if (!localStorage.getItem('atp.studio.theme')) localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('defaultCliType', 'claude');
  });
  await page.goto('/');
  const dismiss = page.getByTestId('crash-recovery-dismiss').first();
  if (await dismiss.isVisible().catch(() => false)) await dismiss.click({ force: true });
  const keys = new FocusWalk(page);

  // Open the Dossier from Needs you: focus lands on its heading, not the body.
  await page.getByTestId('studio-attention-trigger').focus();
  await page.keyboard.press('Enter');
  const row = page.getByTestId('studio-attention-list').getByTestId('attention-row')
    .filter({ hasText: 'Export retention' }).first();
  await row.getByTestId('attention-row-action').focus();
  await page.keyboard.press('Enter');
  const overviewOpen = page.getByTestId(`workbench-overview-open-${PROJECT}-${WORKBENCH_ID}`);
  if (!await page.getByTestId('workbench-viewer').isVisible().catch(() => false)
      && await overviewOpen.isVisible({ timeout: 5_000 }).catch(() => false)) {
    await overviewOpen.focus();
    await page.keyboard.press('Enter');
  }
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });
  await expect.poll(() => keys.describe()).toBe('workbench-viewer-title');
  await page.screenshot({ path: resolve(RESULTS, 'keyboard-dossier-heading.png') });

  // Unanswered: Tab from the last decision note leaves the frame.
  const firstPoint = 'frame:radio:window-7';
  await keys.tabUntil(focus => focus.startsWith('frame:radio:'), 40);
  expect(await keys.describe()).toBe(firstPoint);
  await keys.tabUntil(focus => focus === LAST_NOTE, 6);
  await keys.tab();
  expect(keys.last()).not.toMatch(/^frame:/);
  await page.screenshot({ path: resolve(RESULTS, 'keyboard-dossier-left-frame-unanswered.png') });

  // Back into the frame, answer both points with the keyboard.
  await keys.tabUntil(focus => focus.startsWith('frame:radio:window-'), 20, true);
  await page.keyboard.press('ArrowDown');
  await keys.tabUntil(focus => focus.startsWith('frame:radio:proof-'), 4);
  await page.keyboard.press('Space');
  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  await expect(frame.locator('[data-decision-id] input[type="radio"]:checked')).toHaveCount(2);
  const inline = page.getByTestId('workbench-inline-decision-action');
  await expect(inline).toContainText('2 of 2 answered');
  await keys.tabUntil(focus => focus === LAST_NOTE, 4);
  await keys.tab();
  expect(keys.last()).toMatch(/^workbench-decision-(rework|start)$/);
  await keys.tabUntil(focus => focus === 'workbench-decision-start', 4);

  // The form takes focus, Escape hands it back, and Tab from the frame reaches the form.
  await page.keyboard.press('Enter');
  await expect.poll(() => keys.describe()).toBe('workbench-decision-title');
  await page.keyboard.press('Escape');
  await expect.poll(() => keys.describe()).toBe('workbench-decision-start');
  await page.keyboard.press('Enter');
  await expect.poll(() => keys.describe()).toBe('workbench-decision-title');
  await keys.tabUntil(focus => focus === LAST_NOTE, 20, true);
  await keys.tab();
  expect(keys.last()).toBe('workbench-decision-title');
  await page.keyboard.press('ControlOrMeta+a');
  await page.keyboard.type(`Implement export retention by keyboard ${Date.now().toString(36)}`);
  await page.screenshot({ path: resolve(RESULTS, 'keyboard-dossier-form.png') });
  await keys.tabUntil(focus => focus === 'workbench-decision-confirm', 20);
  await expect(inline.getByTestId('workbench-decision-confirm')).toBeEnabled({ timeout: 30_000 });

  // After Confirm, focus lands on the receipt and Tab moves on from it.
  await page.keyboard.press('Enter');
  await expect(inline.getByTestId('workbench-decision-created-tasks')).toBeVisible({ timeout: 30_000 });
  await expect.poll(() => keys.describe()).toBe('workbench-decision-receipt');
  await page.screenshot({ path: resolve(RESULTS, 'keyboard-dossier-receipt.png') });
  await keys.tab();
  expect((await readWorkbench(request)).workbench.status).toBe('decided');
  writeFileSync(resolve(RESULTS, 'keyboard-dossier-focus-walk.txt'), keys.log.join('\n') + '\n');
});

const LAST_NOTE = 'frame:textarea:deletion-proof';

/** Records every focus stop; a Tab press may not land on the body or stay on its control. */
class FocusWalk {
  readonly log: string[] = [];
  private readonly stops: string[] = [];
  constructor(private readonly page: Page) {}

  last(): string { return this.stops.at(-1) ?? ''; }

  async tab(reverse = false): Promise<string> {
    const before = await this.describe();
    await this.page.keyboard.press(reverse ? 'Shift+Tab' : 'Tab');
    const after = await this.describe();
    this.stops.push(after);
    this.log.push(`${reverse ? 'shift+tab' : 'tab'} ${after}`);
    expect(after, `focus after ${reverse ? 'Shift+Tab' : 'Tab'} from ${before}`).not.toBe('body');
    expect(after, `Tab from ${before} did not move focus`).not.toBe(before);
    return after;
  }

  async tabUntil(done: (focus: string) => boolean, limit: number, reverse = false): Promise<void> {
    for (let press = 0; press < limit; press++) if (done(await this.tab(reverse))) return;
    throw new Error(`Focus did not reach the target within ${limit} presses:\n${this.log.slice(-limit).join('\n')}`);
  }

  /** `frame:<kind>:<decision or option>` inside the artifact, else the host control's test id. */
  async describe(): Promise<string> {
    const host = await this.page.evaluate(() => {
      const active = document.activeElement as HTMLElement | null;
      if (!active || active === document.body) return 'body';
      if (active.matches('[data-testid="workbench-viewer-frame"]')) return 'frame';
      return active.getAttribute('data-testid') || `${active.tagName.toLowerCase()}#${active.id}:${(active.getAttribute('aria-label') || active.textContent || '').trim().slice(0, 40)}`;
    });
    if (host !== 'frame') return host;
    const frame = await (await this.page.getByTestId('workbench-viewer-frame').elementHandle())?.contentFrame();
    return await frame!.evaluate(() => {
      const active = document.activeElement as HTMLElement | null;
      if (!active || active === document.body) return 'frame:body';
      const option = active.closest('[data-option-id]')?.getAttribute('data-option-id');
      const point = active.closest('[data-decision-id]')?.getAttribute('data-decision-id');
      const kind = active instanceof HTMLInputElement ? active.type : active.tagName.toLowerCase();
      return `frame:${kind}:${option ?? point ?? (active.textContent || '').trim().slice(0, 40)}`;
    });
  }
}

async function openDossier(page: Page): Promise<void> {
  await page.addInitScript(({ project, workbenchId }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'workbench', projectName: project, workbenchId, title: 'Export retention and privacy' }],
      activeKey: `workbench:${project}:${workbenchId}`,
    }));
    if (!localStorage.getItem('atp.studio.theme')) localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('defaultCliType', 'claude');
  }, { project: PROJECT, workbenchId: WORKBENCH_ID });
  await page.goto('/');
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });
  const dismiss = page.getByTestId('crash-recovery-dismiss').first();
  if (await dismiss.isVisible().catch(() => false)) await dismiss.click({ force: true });
}

async function answerBothDecisions(page: Page): Promise<void> {
  await restoreAnswers(page);
  await revealAction(page);
  await expect(page.getByTestId('workbench-inline-decision-action')
    .getByTestId('workbench-decision-start')).toBeEnabled({ timeout: 30_000 });
}

/**
 * Selects the recommended option of both decision points. The viewer iframe
 * reloads when the theme or the viewport changes, which drops the answers it
 * holds, so the retry after the screenshots selects them again.
 */
async function restoreAnswers(page: Page): Promise<void> {
  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  for (const option of ANSWERED_OPTIONS) {
    const input = frame.locator(`[data-option-id="${option}"] input`);
    await expect(input).toBeVisible({ timeout: 30_000 });
    if (!await input.isChecked()) await input.check();
  }
}

/** Bring the inline action into view even when the artifact has no host slot. */
async function revealAction(page: Page, block: ScrollLogicalPosition = 'center'): Promise<void> {
  await page.getByTestId('workbench-inline-decision-action')
    .evaluate((element, position) => element.scrollIntoView({ block: position }), block);
}

async function capture(page: Page, name: string): Promise<void> {
  const updateNotice = page.getByRole('status').filter({ hasText: 'Agent Studio was updated' });
  if (await updateNotice.isVisible()) await updateNotice.getByRole('button', { name: 'Dismiss' }).click();
  const viewports = [{ suffix: '1280', width: 1280, height: 900 }, { suffix: '390', width: 390, height: 1200 }];
  for (const theme of ['light', 'dark'] as Theme[]) {
    await setTheme(page, theme);
    await page.emulateMedia({ colorScheme: theme });
    for (const viewport of viewports) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await revealAction(page, 'start');
      // The frame republishes the anchor on the next animation frame after a resize.
      const panel = page.getByTestId('workbench-inline-decision-action');
      await expect.poll(async () => {
        const box = await panel.boundingBox();
        return box ? box.x + box.width : Number.POSITIVE_INFINITY;
      }, { timeout: 10_000 }).toBeLessThanOrEqual(viewport.width + 1);
      await expect.poll(() => panel.evaluate(element => getComputedStyle(element).opacity))
        .toBe('1');
      await page.screenshot({ path: resolve(RESULTS, `${name}-${theme}-${viewport.suffix}.png`) });
    }
  }
  await setTheme(page, 'light');
  await page.emulateMedia({ colorScheme: 'light' });
  await page.setViewportSize({ width: 1280, height: 900 });
}

/**
 * Decisions require a committed descriptor. Restore this Dossier from the
 * repository's seed commit between scenarios because the demo stack is shared.
 * Test commits name their own identity, so Studio still has no host git identity.
 */
function commitSeededDocs(repository: string): void {
  execFileSync('git', ['-C', repository, 'add', '--', 'docs'], { encoding: 'utf8' });
  const staged = execFileSync('git', ['-C', repository, 'diff', '--cached', '--name-only'], { encoding: 'utf8' });
  if (staged.trim()) {
    execFileSync('git', ['-C', repository, '-c', 'user.name=Demo Seed', '-c', 'user.email=seed@demo.invalid',
      'commit', '-q', '-m', 'seed: demo Dossiers'], { encoding: 'utf8' });
  }
  const seed = execFileSync('git', ['-C', repository, 'rev-list', '--max-parents=0', 'HEAD'],
    { encoding: 'utf8' }).trim();
  expect(seed).not.toBe('');
  execFileSync('git', ['-C', repository, 'restore', `--source=${seed}`, '--staged', '--worktree',
    '--', 'docs/dossiers/export-retention/workbench.json'], { encoding: 'utf8' });
  const reset = execFileSync('git', ['-C', repository, 'diff', '--cached', '--name-only'], { encoding: 'utf8' });
  if (reset.trim()) {
    execFileSync('git', ['-C', repository, '-c', 'user.name=Demo Seed', '-c', 'user.email=seed@demo.invalid',
      'commit', '-q', '-m', 'test: restore export retention Dossier'], { encoding: 'utf8' });
  }
}

function branchLock(repository: string): string {
  const head = execFileSync('git', ['-C', repository, 'symbolic-ref', '--short', 'HEAD'], { encoding: 'utf8' }).trim();
  const lock = resolve(repository, '.git', 'refs', 'heads', `${head}.lock`);
  expect(existsSync(lock)).toBe(false);
  return lock;
}

async function readWorkbench(request: APIRequestContext): Promise<WorkbenchRead> {
  const response = await request.get(
    `/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}`);
  expect(response.ok()).toBe(true);
  return await response.json() as WorkbenchRead;
}

async function boardTitles(request: APIRequestContext): Promise<string[]> {
  const response = await request.get('/api/v1/studio/board');
  expect(response.ok()).toBe(true);
  const board = await response.json() as Record<string, BoardCard[] | unknown>;
  return Object.values(board)
    .filter((lane): lane is BoardCard[] => Array.isArray(lane))
    .flat()
    .map(card => card.title ?? '');
}
