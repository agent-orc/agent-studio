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
    expect(await boardTitles(request)).not.toContain(title);
    expect((await readWorkbench(request)).workbench.decision ?? null).toBeNull();
    await capture(page, 'dossier-decision-failed');
  } finally {
    rmSync(lock, { force: true });
  }

  // The retry creates a fresh card and records the decision with it.
  await revealAction(page);
  await inline.getByTestId('workbench-decision-confirm').click();
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
  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  for (const option of ['window-30', 'proof-receipt']) {
    const input = frame.locator(`[data-option-id="${option}"] input`);
    await expect(input).toBeVisible({ timeout: 30_000 });
    await input.check();
  }
  await revealAction(page);
  await expect(page.getByTestId('workbench-inline-decision-action')
    .getByTestId('workbench-decision-start')).toBeEnabled({ timeout: 30_000 });
}

/** The inline action floats over the slot after the last decision point. */
async function revealAction(page: Page, block: ScrollLogicalPosition = 'center'): Promise<void> {
  const slot = page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .locator('[data-studio-decision-host-slot]');
  await slot.evaluate((element, position) => element.scrollIntoView({ block: position }), block);
}

async function capture(page: Page, name: string): Promise<void> {
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
 * The demo seed leaves its Dossiers uncommitted, and decisions require a
 * committed descriptor. The seed commit names its own identity per command,
 * so the host still has no git identity when Studio writes the decision.
 */
function commitSeededDocs(repository: string): void {
  execFileSync('git', ['-C', repository, 'add', '--', 'docs'], { encoding: 'utf8' });
  const staged = execFileSync('git', ['-C', repository, 'diff', '--cached', '--name-only'], { encoding: 'utf8' });
  if (!staged.trim()) return;
  execFileSync('git', ['-C', repository, '-c', 'user.name=Demo Seed', '-c', 'user.email=seed@demo.invalid',
    'commit', '-q', '-m', 'seed: demo Dossiers'], { encoding: 'utf8' });
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
