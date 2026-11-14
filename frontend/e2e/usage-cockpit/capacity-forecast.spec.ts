import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme, dismissDevErrorDialog } from '../helpers/theme';
import { SNAPSHOT, FIXTURE_NOW } from '../../src/mockups/usage-chips/app/usage-chips.fixtures';

/**
 * AGT-3584: the usage detail states what is left of each quota window and
 * lists the Ready cards whose configured model would fall back at the current
 * quota, from GET /api/operator/capacity. A card pinned to a model the
 * installed CLI does not offer is flagged as not offered.
 */
const shots = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'capacity');
const NOT_OFFERED = 'Not offered by the installed claude-code 2.1.300.';

function cockpit() {
  const snapshot = structuredClone(SNAPSHOT);
  snapshot.clis = snapshot.clis.map(cli => cli.cliId !== 'claude' ? cli : ({
    ...cli,
    windows: cli.windows.map(window => window.id.includes('weekly') ? { ...window, usedPct: 97 } : window),
  }));
  return snapshot;
}

function queuedCard(cardKey: string, configuredModel: string, title: string) {
  return {
    project: 'sample', cardKey, title,
    configuredCli: 'claude', configuredModel, configuredThinkingLevel: 'high',
    outcome: 'fallback', targetCli: 'codex', targetModel: 'gpt-6-sol', targetThinkingLevel: 'high',
    reason: 'Weekly (all models) at 97% is projected to reach the cap before the run ends.',
    nextResetAt: '2026-09-30T08:00:00Z',
  };
}

const capacity = {
  quota: [], activeFallbacks: [], waitingCards: [], modelRoutingPins: [],
  queuedCards: [
    queuedCard('SAMPLE-11', 'claude-opus-5', 'Split the import parser'),
    queuedCard('SAMPLE-12', 'claude-opus-4-8', 'Repair the export retry'),
  ],
};

const claudeCatalog = {
  source: 'claude-code 2.1.300',
  models: [
    { id: 'claude-opus-5', label: 'Opus 5', multiplier: null, vendor: 'anthropic', isDefault: true },
    { id: 'claude-opus-4-8', label: 'Opus 4.8', multiplier: null, vendor: 'anthropic', isDefault: false,
      available: false, availabilityNote: NOT_OFFERED },
  ],
};

test('near-exhausted window shows what is left and the two Ready cards that would fall back', async ({ page }) => {
  test.setTimeout(240_000);
  page.setDefaultNavigationTimeout(60_000);
  mkdirSync(shots, { recursive: true });
  await page.clock.setFixedTime(FIXTURE_NOW);
  let capacityReads = 0;
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: cockpit() }));
  await page.route('**/api/operator/capacity', route => {
    capacityReads += 1;
    return route.fulfill({ json: capacity });
  });
  await page.route('**/api/cli/claude/models**', route => route.fulfill({ json: claudeCatalog }));
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');
  await dismissDevErrorDialog(page);

  const footer = page.getByTestId('status-bar');
  await expect(footer.getByTestId('footer-usage-claude')).toContainText('97%', { timeout: 60_000 });
  // The capacity view is read only while the detail is open.
  expect(capacityReads).toBe(0);
  await footer.getByTestId('footer-usage-claude').click();
  const surface = page.getByTestId('usage-detail-surface');
  await expect(surface).toBeVisible();

  const weekly = surface.getByTestId('usage-detail-windows-claude').locator('[data-window-id="claude/weekly-(all-models)"]');
  await expect(weekly).toContainText('97% used');
  await expect(weekly.getByTestId('usage-detail-window-left')).toHaveText('3% left');

  const section = surface.getByTestId('usage-detail-capacity');
  await surface.getByTestId('usage-detail-jump-capacity').click();
  await expect(section.getByTestId('usage-detail-capacity-summary')).toHaveText('2 would fall back');
  const cards = section.getByTestId('usage-detail-capacity-card');
  await expect(cards).toHaveCount(2);
  for (const card of await cards.all()) {
    await expect(card).toHaveAttribute('data-outcome', 'fallback');
    await expect(card.getByTestId('usage-detail-capacity-outcome')).toHaveText('Would fall back');
    await expect(card.getByTestId('usage-detail-capacity-target')).toHaveText('codex / gpt-6-sol / high');
    await expect(card.getByTestId('usage-detail-capacity-reason')).toContainText('Weekly (all models) at 97%');
  }
  const current = section.locator('[data-card-key="SAMPLE-11"]');
  const retired = section.locator('[data-card-key="SAMPLE-12"]');
  await expect(current.getByTestId('usage-detail-capacity-configured')).toHaveText('claude / claude-opus-5 / high');
  await expect(current.getByTestId('usage-detail-capacity-not-offered')).toHaveCount(0);
  await expect(retired.getByTestId('usage-detail-capacity-configured')).toHaveText('claude / claude-opus-4-8 / high');
  await expect(retired.getByTestId('usage-detail-capacity-not-offered')).toHaveText('Not offered');
  await expect(section).not.toContainText('update available');
  expect(capacityReads).toBeGreaterThan(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await surface.getByTestId('usage-detail-jump-capacity').click();
    await page.mouse.move(1, 1);
    await surface.screenshot({ path: join(shots, `usage-detail-capacity-1440-${theme}.png`), animations: 'disabled' });
  }

  await page.getByTestId('usage-detail-close').click();
  await page.setViewportSize({ width: 390, height: 900 });
  await footer.getByTestId('footer-usage-claude').click();
  await expect(surface).toHaveAttribute('data-mode', 'sheet');
  await surface.getByTestId('usage-detail-jump-capacity').click();
  await expect(cards).toHaveCount(2);
  await expect.poll(() => surface.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(2);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(shots, `usage-detail-capacity-390-${theme}.png`), animations: 'disabled' });
  }
});
