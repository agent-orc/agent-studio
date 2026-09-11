import { expect, test, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

test.use({ launchOptions: { executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' } });
test.setTimeout(8 * 60_000);

const OUT = 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/tasks/002/AGT-2072/results/sweep-2026-07-11';
const captured: Array<{ file: string; surface: string; state: string; route: string; theme: string; viewport: string; source: 'real' }> = [];
const missingStates: Array<{ surface: string; state: string; reason: string; route: string }> = [];

function safe(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 80);
}

async function setTheme(page: Page, theme: 'dark' | 'light') {
  await page.evaluate(t => {
    document.documentElement.dataset['studioTheme'] = t;
    localStorage.setItem('atp.studio.theme', t);
  }, theme);
}

async function capture(page: Page, surface: string, state: string) {
  for (const viewport of [
    { name: 'desktop', width: 1440, height: 1000 },
    { name: 'narrow', width: 430, height: 900 },
  ]) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await page.waitForTimeout(180);
      const file = `${safe(surface)}--${safe(state)}--${viewport.name}--${theme}--real.png`;
      await page.screenshot({ path: join(OUT, file), fullPage: false });
      captured.push({ file, surface, state, route: page.url(), theme, viewport: viewport.name, source: 'real' });
    }
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
}

async function recordMissing(page: Page, surface: string, state: string, reason: string) {
  missingStates.push({ surface, state, reason, route: page.url() });
}

async function dismissRecovery(page: Page) {
  const overlay = page.getByTestId('crash-recovery-prompt-overlay');
  const visible = await overlay.waitFor({ state: 'visible', timeout: 8_000 }).then(() => true).catch(() => false);
  if (visible) {
    await setTheme(page, 'dark');
    const file = 'board--crash-recovery-49-files--desktop--dark--real.png';
    await page.screenshot({ path: join(OUT, file), fullPage: false });
    captured.push({ file, surface: 'Board', state: 'Crash recovery modal with 49 files', route: page.url(), theme: 'dark', viewport: 'desktop', source: 'real' });
    // Stable ships this dialog as closable=false. Hide only the browser-side
    // overlay so the sweep does not dismiss or commit operator recovery data.
    await overlay.evaluate(element => {
      (element as HTMLElement).style.display = 'none';
      (element as HTMLElement).style.pointerEvents = 'none';
    });
  } else {
    await recordMissing(page, 'Board', 'Crash recovery modal with 49 files', 'Modal was not present at capture time.');
  }
}

async function captureActivitySurface(page: Page, testid: string, surface: string) {
  const button = page.getByTestId(testid);
  if (!await button.isVisible().catch(() => false)) {
    await recordMissing(page, surface, 'open', `Control ${testid} was not visible.`);
    return;
  }
  await button.click();
  await page.waitForTimeout(700);
  await capture(page, surface, 'open');
}

test('App-Vermessung v1 real stable sweep', async ({ page }) => {
  mkdirSync(OUT, { recursive: true });
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4_000);
  await dismissRecovery(page);

  await capture(page, 'Board', 'All projects all lanes explorer open');
  const body = await page.locator('body').innerText();
  const lanePreflight: Record<string, number | null> = {};
  for (const [key, label] of Object.entries({
    '2-ready': 'Ready', '1-preparation': 'In Preparation', '0-backlog': 'Backlog',
    '3-progress': 'In Progress', '4-auto-review': 'Post Processing', '5-human-review': 'Human review',
    '6-escalation': 'Escalated', '7-delivered': 'Delivered', '8-archive': 'Archive',
  })) {
    const match = new RegExp(`${label}\\s+(\\d+)`, 'i').exec(body);
    lanePreflight[key] = match ? Number(match[1]) : null;
  }
  const anomaly = (lanePreflight['5-human-review'] ?? 0) >= 100
    && lanePreflight['7-delivered'] === 0
    && (lanePreflight['8-archive'] ?? 0) >= 100;
  for (const [key, count] of Object.entries(lanePreflight)) {
    if (count === 0) {
      await recordMissing(page, 'Board lane data', key, 'Stable reported zero real cards. The state was documented and not fabricated.');
    }
  }

  const explorerToggle = page.getByTestId('studio-ab-explorer');
  if (await explorerToggle.isVisible().catch(() => false)) {
    await explorerToggle.click();
    await page.waitForTimeout(350);
    await capture(page, 'Explorer', 'closed');
    await explorerToggle.click();
    await page.waitForTimeout(350);
  } else {
    await recordMissing(page, 'Explorer', 'closed', 'Explorer toggle was not visible.');
  }

  const search = page.getByTestId('studio-global-search-trigger');
  if (await search.isVisible().catch(() => false)) {
    await search.click();
    await page.waitForTimeout(400);
    await capture(page, 'Global search', 'empty query');
    const searchBackdrop = page.getByTestId('global-search-backdrop');
    if (await searchBackdrop.isVisible().catch(() => false)) {
      await searchBackdrop.click({ position: { x: 4, y: 4 } });
      await searchBackdrop.waitFor({ state: 'hidden', timeout: 3_000 }).catch(() => undefined);
    }
  } else {
    await recordMissing(page, 'Global search', 'empty query', 'Search trigger was not visible.');
  }

  const reviewTask = page.getByText('AGT-1944', { exact: true }).first();
  if (await reviewTask.isVisible().catch(() => false)) {
    await reviewTask.click();
    await page.waitForTimeout(1_000);
    await capture(page, 'Task detail', 'escalated review overview');
    const detailControls = await page.locator('[data-testid^="inspector-tab-"], [data-testid^="detail-tab-"], [data-testid^="pane-toggle-"]').evaluateAll(nodes =>
      nodes.map(node => ({ id: node.getAttribute('data-testid') ?? '', label: (node.textContent ?? '').trim() })).filter(x => x.id));
    for (const control of detailControls.slice(0, 12)) {
      const item = page.getByTestId(control.id).first();
      if (await item.isVisible().catch(() => false)) {
        await item.click().catch(() => undefined);
        await page.waitForTimeout(450);
        await capture(page, 'Task detail', control.label || control.id);
      }
    }
  } else {
    await recordMissing(page, 'Task detail', 'escalated review overview', 'The real escalated AGT-1944 card was not visible.');
  }

  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1_500);
  await dismissRecovery(page);
  const projectRow = page.getByTestId('studio-explorer-project-Agent Studio');
  const hubButton = projectRow.getByRole('button', { name: 'Open Deck' });
  if (await hubButton.isVisible().catch(() => false)) {
    await hubButton.click();
    await page.waitForTimeout(1_200);
    await capture(page, 'Deck', 'overview');
    const rails = await page.locator('[data-testid^="project-shell-rail-"]').evaluateAll(nodes =>
      nodes.map(node => ({ id: node.getAttribute('data-testid') ?? '', label: (node.textContent ?? '').trim() })).filter(x => x.id));
    for (const rail of rails.slice(0, 14)) {
      const item = page.getByTestId(rail.id).first();
      if (await item.isVisible().catch(() => false)) {
        await item.click().catch(() => undefined);
        await page.waitForTimeout(600);
        const state = rail.label || rail.id;
        const surface = /wiki/i.test(state) ? 'Wiki and Pulse' : 'Deck';
        await capture(page, surface, state);
      }
    }
  } else {
    await recordMissing(page, 'Deck', 'overview', 'Agent Studio Deck control was not visible.');
  }

  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1_200);
  await dismissRecovery(page);
  await captureActivitySurface(page, 'studio-ab-activity', 'Activity feed');
  await captureActivitySurface(page, 'studio-ab-runbook', 'Runbook');
  await captureActivitySurface(page, 'studio-ab-admin', 'Orchestrator administration');

  const usage = page.getByText('Usage', { exact: true }).last();
  if (await usage.isVisible().catch(() => false)) {
    await usage.click();
    await page.waitForTimeout(700);
    await capture(page, 'Usage', 'status bar entry');
  } else {
    await recordMissing(page, 'Usage', 'status bar entry', 'Usage status control was not visible.');
  }

  for (const key of ['overview', 'appearance', 'updates', 'workspaces', 'caps', 'working-memory', 'prompts', 'tokens', 'screenshots', 'remote-hosts']) {
    await page.goto(`/#/workspace/settings/${key}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(850);
    const recoveryOverlay = page.getByTestId('crash-recovery-prompt-overlay');
    if (await recoveryOverlay.isVisible().catch(() => false)) {
      await recoveryOverlay.evaluate(element => {
        (element as HTMLElement).style.display = 'none';
        (element as HTMLElement).style.pointerEvents = 'none';
      });
    }
    const rail = page.getByTestId(`workspace-settings-rail-${key}`);
    if (await rail.isVisible().catch(() => false)) {
      await capture(page, key === 'remote-hosts' ? 'Execution Hosts' : 'Settings', key);
    } else {
      await recordMissing(page, key === 'remote-hosts' ? 'Execution Hosts' : 'Settings', key, 'Settings rail was not visible in real Stable.');
    }
  }

  const manifest = {
    schemaVersion: 1,
    capturedAt: new Date().toISOString(),
    target: 'http://localhost:4011',
    source: 'real Stable operator data',
    viewportContract: { desktop: { width: 1440, height: 1000 }, narrow: { width: 430, height: 900 }, deviceScaleFactor: 1 },
    preflight: {
      laneCardCounts: lanePreflight,
      allProjectsReviewDeliveredAnomalyObserved: anomaly,
      conceptSource: 'docs/concepts/visual-quality-and-proof.md was absent from the assigned worktree',
      recoveryHandling: 'Stable rendered the dialog with closable=false. After its evidence shot, Playwright hid only the browser DOM overlay; no dismiss or commit API was called.',
    },
    files: Array.from(new Map(captured.map(item => [item.file, item])).values()),
    missingStates,
  };
  writeFileSync(join(OUT, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n', 'utf8');
  expect(captured.length).toBeGreaterThan(20);
});
