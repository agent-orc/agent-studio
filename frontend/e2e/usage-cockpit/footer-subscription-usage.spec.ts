import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme, dismissDevErrorDialog } from '../helpers/theme';
import { SNAPSHOT, GEMINI_UNAVAILABLE, FIXTURE_NOW } from '../../src/mockups/usage-chips/app/usage-chips.fixtures';

const shots = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'bars');
const observedAt = new Date(FIXTURE_NOW).toISOString();

function capability(key: string, invalid = false) {
  return {
    key, category: key.split(':')[0], advertisedStatus: invalid ? 'unavailable' : 'ready',
    healthState: invalid ? 'unhealthy' : 'healthy', healthOutcome: invalid ? 'credential_invalid' : 'healthy',
    signal: invalid ? 'signed-out' : 'signed-in', reason: invalid ? 'Sign in again' : null,
    advertisedAt: observedAt, freshUntil: new Date(FIXTURE_NOW + 120_000).toISOString(),
    isFresh: true, consecutiveFailures: invalid ? 2 : 0, detail: invalid ? 'Sign in again' : 'Probe completed',
    recoveryHistory: [],
  };
}

function hosts(invalid: boolean) {
  return ['agent-runner-01', 'agent-runner-01-review', 'kori-review', 'kori-review-2', 'kori-coding']
    .map((name, index) => ({
      runnerId: name, name, hostId: `host-${index}`, instanceId: `instance-${index}`,
      runnerVersion: '1', protocolVersion: 2, status: 'active',
      registeredAt: observedAt, lastSeenAt: observedAt,
      hostAdmission: { hostId: `host-${index}`, admissionState: 'open' },
      capabilities: [capability('provider-auth:codex'),
        capability('provider-auth:claude', invalid && name === 'kori-review'),
        capability('model-acceptance:claude:claude-opus', false)],
    }));
}

function cockpit() {
  const snapshot = structuredClone(SNAPSHOT);
  snapshot.cost.oneShotSources = [{ source: 'auto-tag', todayUsd: 0.01, weekUsd: 0.04 }];
  snapshot.clis = [...snapshot.clis, GEMINI_UNAVAILABLE].map(cli => cli.cliId === 'gemini' ? cli : ({
    ...cli,
    windows: cli.cliId === 'codex' ? cli.windows : cli.windows.filter(window => window.id.includes('weekly')),
    ...(cli.cliId === 'claude' ? {
      fetchedAt: new Date(FIXTURE_NOW - 3_600_000).toISOString(),
      availability: { ...cli.availability, status: 'stale' },
    } : {}),
  }));
  return snapshot;
}

test('idle ledger remains current while partial coverage names missing prices and sources', async ({ page }) => {
  mkdirSync(shots, { recursive: true });
  await page.clock.setFixedTime(FIXTURE_NOW);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
  } }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: hosts(false) }));
  await page.route('**/api/v1/management/links', route => route.fulfill({ json: [] }));
  await page.route('**/api/usage/cockpit**', route => {
    const snapshot = cockpit();
    const ledger = snapshot.cost!;
    ledger.weeklyBudgetUsd = 100;
    ledger.budgetWeekSpentUsd = 25;
    ledger.unpricedWeekRuns = 1;
    ledger.projects[0].weeklyBudgetUsd = 50;
    ledger.projects[0].budgetWeekSpentUsd = 10;
    ledger.coverage = {
      status: 'partial', observedAt, ttlSeconds: 60,
      reason: 'Demo Platform: unknown-model has no historical USD price. Missing Source: The project ledger could not be read.',
    };
    ledger.latestReceiptAt = new Date(FIXTURE_NOW - 5 * 3_600_000).toISOString();
    return route.fulfill({ json: snapshot });
  });
  await page.goto('/');
  await dismissDevErrorDialog(page);
  const cost = page.getByTestId('footer-usage-cost');
  await expect(cost).toHaveAttribute('data-state', 'partial');
  await expect(cost).toContainText('Partial');
  await expect(cost).not.toContainText('old');
  await expect(cost).toHaveAttribute('title', /Demo Platform: unknown-model.*Missing Source/);
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(shots, `partial-ledger-${theme}--mocked.png`), animations: 'disabled' });
  }
  await cost.click();
  const detail = page.getByTestId('usage-detail-cost');
  await expect(detail).toContainText('Latest receipt');
  await expect(detail).toContainText('$25.00 priced spend / weekly USD budget $100.00, $75.00 remaining');
  await expect(detail).toContainText('$10.00 priced spend / weekly budget $50.00, $40.00 remaining');
  await expect(detail).toContainText('Unpriced usage is outside the budget');
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    const surface = page.getByTestId('usage-detail-surface');
    await expect(surface).toBeVisible();
    await surface.getByTestId('usage-detail-jump-cost').click();
    await surface.screenshot({ path: join(shots, `weekly-budget-${theme}--mocked.png`) });
  }
});

test('one footer entry per subscription, honest age, host problem, account and responsive bars', async ({ page }) => {
  test.setTimeout(240_000);
  page.setDefaultNavigationTimeout(60_000);
  mkdirSync(shots, { recursive: true });
  await page.clock.setFixedTime(FIXTURE_NOW);
  let invalid = false;
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
  } }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: hosts(invalid) }));
  await page.route('**/api/v1/management/links', route => route.fulfill({ json: [] }));
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: cockpit() }));
  await page.setViewportSize({ width: 1920, height: 900 });
  await page.goto('/');
  await dismissDevErrorDialog(page);

  const footer = page.getByTestId('status-bar');
  await expect(footer.getByTestId('footer-usage-claude')).toContainText('WK 7%', { timeout: 60_000 });
  await expect(footer.getByTestId('footer-usage-codex')).toContainText('WK 15%');
  await expect(footer.getByTestId('footer-usage-claude')).toContainText('1 h old');
  await expect(footer.getByTestId('footer-usage-claude')).toHaveAttribute('title', /Refresh the usage source or check the provider connection in Execution Hosts/);
  await expect(footer.getByTestId('footer-usage-claude')).not.toContainText('Session');
  await expect(footer.getByTestId('footer-usage-cost')).toContainText('$12.48');
  const usageEntries = footer.locator('[data-usage-trigger]');
  await expect(usageEntries).toHaveCount(3);
  for (const entry of await usageEntries.all()) {
    await expect(entry).toHaveJSProperty('tagName', await entry.getAttribute('data-usage-trigger') === 'cost' ? 'BUTTON' : 'A');
  }
  await expect(footer.locator('[data-testid^="footer-usage-claude"]')).toHaveCount(1);
  await expect(footer.locator('[data-testid^="footer-usage-codex"]')).toHaveCount(1);
  await expect(footer).not.toContainText('kori-review');
  await expect(footer.getByTestId('footer-host-problem-claude')).toHaveCount(0);
  await expect(page.getByTestId('cockpit-header-usage-row')).toHaveCount(0);
  await expect(page.getByTestId('studio-titlebar').locator('[data-usage-trigger]')).toHaveCount(0);
  await expect(page.getByTestId('studio-titlebar-settings')).toBeVisible();
  await expect(page.getByTestId('studio-titlebar-defaults')).toBeVisible();
  await expect(page.getByTestId('studio-titlebar-account')).toContainText('Owner');
  await expect(page.getByTestId('studio-ab-settings')).toHaveCount(0);
  await page.getByTestId('studio-titlebar-account').click();
  await expect(page.getByTestId('studio-account-menu-item-sign-out')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(footer.getByText('Settings', { exact: true })).toHaveCount(0);

  await footer.getByTestId('footer-usage-cost').click();
  await expect(page.getByTestId('usage-detail-surface')).toBeVisible();
  await expect(page.getByTestId('usage-detail-jump-gemini')).toBeVisible();
  await expect(page.getByTestId('usage-detail-windows-claude')).toContainText('N/A');
  await expect(page.getByTestId('usage-detail-chat-toggle')).toBeVisible();
  await page.getByTestId('usage-detail-close').click();

  await footer.getByTestId('footer-usage-cost').click();
  await expect(page.getByTestId('usage-detail-one-shot-sources')).toContainText('auto-tag');
  await page.getByTestId('usage-detail-one-shot-sources').screenshot({
    path: join(shots, 'footer-one-shot-cost--mocked.png'),
  });
  await page.getByTestId('usage-detail-close').click();

  for (const width of [1920, 1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const layout = await footer.evaluate(bar => {
      const visible = (element: Element) => {
        const box = element.getBoundingClientRect();
        return box.width > 0 && box.height > 0 && getComputedStyle(element).visibility !== 'hidden';
      };
      const children = [...bar.children].filter(visible);
      const separator = bar.querySelector('[data-testid="status-bar-separator"]');
      const entries = [...bar.querySelectorAll('[data-usage-trigger]')].filter(visible);
      const right = bar.querySelector('[data-testid="status-bar-usage-entries"]')!.getBoundingClientRect();
      const barBox = bar.getBoundingClientRect();
      return {
        separatorBetweenGroups: !separator || !visible(separator)
          || (children.indexOf(separator) > 0 && children.indexOf(separator) < children.length - 1),
        entryGaps: entries.slice(1).map((entry, index) =>
          entry.getBoundingClientRect().left - entries[index].getBoundingClientRect().right),
        rightEdgeGap: barBox.right - right.right,
        leftBeforeRight: bar.querySelector('[data-testid="status-bar-running"]')!
          .getBoundingClientRect().left < right.left,
        leftAboveRight: bar.querySelector('[data-testid="status-bar-running"]')!
          .getBoundingClientRect().bottom <= right.top,
      };
    });
    expect(layout.separatorBetweenGroups).toBe(true);
    expect(layout.entryGaps.every(gap => gap >= 7)).toBe(true);
    expect(width === 390 ? layout.leftAboveRight : layout.leftBeforeRight).toBe(true);
    if (width >= 1280) expect(layout.rightEdgeGap).toBeLessThanOrEqual(16);
    if (width >= 1280) {
      const titlebar = await page.getByTestId('studio-titlebar').boundingBox();
      const body = await page.getByTestId('studio-body').boundingBox();
      expect(titlebar && body && body.y - titlebar.y - titlebar.height).toBeLessThanOrEqual(56);
    }
    if (width === 390) {
      const usageTarget = await footer.getByTestId('footer-usage-claude').boundingBox();
      expect(usageTarget?.height).toBeGreaterThanOrEqual(44);
    }
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(2);
      if (width === 390) {
        for (const id of ['footer-usage-claude', 'footer-usage-codex', 'footer-usage-cost']) {
          const chip = footer.getByTestId(id);
          const box = await chip.boundingBox();
          expect(box && box.x >= 0 && box.x + box.width <= 390, `${id} visible at 390 px in ${theme}`).toBe(true);
        }
        await expect(footer.getByTestId('footer-usage-claude')).toContainText('WK 7%');
        await expect(footer.getByTestId('footer-usage-codex')).toContainText('WK 15%');
        await expect(footer.getByTestId('footer-usage-cost')).toContainText('$12.48');
        await footer.getByTestId('footer-usage-cost').click();
        const sheet = page.getByTestId('usage-detail-surface');
        await expect(sheet).toHaveAttribute('data-mode', 'sheet');
        await expect(sheet).toBeVisible();
        const sheetBox = await sheet.boundingBox();
        expect(sheetBox && sheetBox.y <= 1 && sheetBox.height >= 899, `full-height sheet in ${theme}`).toBe(true);
        await expect(sheet.getByTestId('usage-detail-windows-codex')).toBeVisible();
        await sheet.screenshot({ path: join(shots, `usage-sheet-390-${theme}.png`), animations: 'disabled' });
        await sheet.getByTestId('usage-detail-jump-cost').click();
        await expect(sheet.getByTestId('usage-detail-today')).toContainText('$12.48');
        await sheet.screenshot({ path: join(shots, `usage-cost-390-${theme}.png`), animations: 'disabled' });
        await page.getByTestId('usage-detail-close').click();
      }
      await page.mouse.move(1, 1);
      await page.screenshot({ path: join(shots, `after-${width}-${theme}.png`), fullPage: false, animations: 'disabled' });
    }
    if (width === 390) {
      await footer.getByTestId('footer-usage-cost').click();
      await expect(page.getByTestId('usage-detail-surface')).toHaveAttribute('data-mode', 'sheet');
      await page.getByTestId('usage-detail-close').click();
    }
  }
  await page.setViewportSize({ width: 320, height: 900 });
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(2);
  for (const id of ['cockpit-header-more', 'studio-titlebar-defaults', 'studio-titlebar-settings', 'studio-titlebar-account']) {
    const target = await page.getByTestId(id).boundingBox();
    expect(target && target.x >= 0 && target.x + target.width <= 320).toBe(true);
  }
  await footer.getByTestId('footer-usage-cost').click();
  await expect(page.getByTestId('usage-detail-surface')).toBeVisible();
  await page.getByTestId('usage-detail-close').click();

  invalid = true;
  await page.reload();
  await dismissDevErrorDialog(page);
  const problem = footer.getByTestId('footer-host-problem-claude');
  await expect(problem).toContainText('Claude: sign-in needed on kori-review');
  await expect(footer.locator('[data-testid^="footer-host-problem-"]')).toHaveCount(1);
  await problem.click();
  await expect(page).toHaveURL(/workspace\/settings\/environment/);
});

test('footer omits the separator when usage has no visible entries', async ({ page }) => {
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: {
    ...cockpit(), clis: [], cost: null, runs: [],
  } }));
  await page.goto('/');
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('status-bar-running')).toBeVisible();
  await expect(page.getByTestId('status-bar-separator')).toHaveCount(0);
});

test('local profile shows Settings without an account control', async ({ page }) => {
  test.setTimeout(90_000);
  page.setDefaultNavigationTimeout(60_000);
  await page.route('**/api/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.goto('/');
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('studio-titlebar-settings')).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('studio-titlebar-defaults')).toBeVisible();
  await expect(page.getByTestId('studio-titlebar-account')).toHaveCount(0);
  await expect(page.getByTestId('status-bar').getByText('Settings', { exact: true })).toHaveCount(0);
});

test('legacy shell keeps usage in the footer and account actions in the header', async ({ page }) => {
  test.setTimeout(90_000);
  await page.addInitScript(() => localStorage.setItem('atp.flag.vsCodeLayout', '0'));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'owner', username: 'owner', displayName: 'Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
  } }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: hosts(false) }));
  await page.route('**/api/v1/management/links', route => route.fulfill({ json: [] }));
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: cockpit() }));
  await page.goto('/');
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('studio-titlebar-settings')).toBeVisible();
  await expect(page.getByTestId('studio-titlebar-account')).toContainText('Owner');
  await expect(page.getByTestId('footer-usage-claude')).toContainText('WK 7%', { timeout: 60_000 });
  await expect(page.locator('header [data-usage-trigger]')).toHaveCount(0);
  await page.getByTestId('footer-usage-cost').click();
  await expect(page.getByTestId('usage-detail-surface')).toBeVisible();
  await page.getByTestId('usage-detail-close').click();
});


test('Fable weekly allowance appears independently in the footer in both themes', async ({ page }) => {
  mkdirSync(shots, { recursive: true });
  await page.clock.setFixedTime(FIXTURE_NOW);
  await page.route('**/api/usage/cockpit**', route => {
    const snapshot = cockpit();
    const claude = snapshot.clis.find(cli => cli.cliId === 'claude')!;
    claude.windows.push({ ...claude.windows[0], id: 'claude/weekly-fable', label: 'Weekly (Fable)', usedPct: 84 });
    return route.fulfill({ json: snapshot });
  });
  await page.goto('/');
  await dismissDevErrorDialog(page);
  const footer = page.getByTestId('footer-usage-claude');
  await expect(footer).toContainText('Fable 84%');
  await expect(footer).toContainText('WK 7%');
  await expect(footer).toHaveAttribute('aria-label', /Fable weekly 84 percent used/);
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await expect(footer).toContainText('Fable 84%');
      const bounds = await footer.boundingBox();
      expect(bounds && bounds.x >= 0 && bounds.x + bounds.width <= width).toBe(true);
      await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(2);
      await page.screenshot({ path: join(shots, `fable-weekly-${width}-${theme}--mocked.png`), animations: 'disabled' });
    }
  }
});
