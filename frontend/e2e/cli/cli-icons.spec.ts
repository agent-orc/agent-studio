import { test, expect } from '@playwright/test';

/**
 * Verifies that each CLI gets a distinct icon glyph in:
 *   1. The cost overview (per-CLI cards in the CLI-Management "Usage caps"
 *      section of the workspace-settings home).
 *   2. The job preview cards on the board.
 *   3. The Command Deck CLI selector and the Add-Task dialog picker.
 *
 * Icons defined in `services/format.util.ts#cliTypeIcon`:
 *   copilot 🐙, claude ✴️, codex 🌀, gemini ♊
 */

const ICONS = {
  copilot: '🐙',
  claude:  '✴️',
  codex:   '🌀',
  gemini:  '♊'
} as const;

test.describe('CLI icons — distinct glyph per CLI', () => {
  test('CLI-Management cards show a per-CLI icon next to each card', async ({ page }) => {
    await page.goto('/');
    // The Usage trigger opens the workspace-settings home at the CLI-Management
    // ("Usage caps") section — the cost overview that succeeded the old
    // quota strip when the loose CLI-usage sidesheet was retired.
    await page.getByTestId('studio-titlebar-settings').click();
    await page.getByTestId('workspace-settings-rail-caps').click();
    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay).toBeVisible();

    const cards = overlay.locator('article[data-cli]');
    await expect(cards.first()).toBeVisible();

    const seen: string[] = [];
    const count = await cards.count();
    for (let i = 0; i < count; i++) {
      const icon = await cards.nth(i).locator('.cli-card__icon').textContent();
      expect(icon?.trim(), `cli-card ${i} should have an icon`).toBeTruthy();
      seen.push(icon!.trim());
    }
    // Every visible card must use one of the four declared glyphs.
    for (const g of seen) {
      expect(Object.values(ICONS)).toContain(g);
    }
  });

  test('Command Deck picker renders distinct icons for each CLI', async ({ page }) => {
    await page.goto('/');
    // Open any job's detail view; the first card on the board will do.
    const firstCard = page.locator('[data-testid="job-card"]').first();
    if (await firstCard.count() === 0) {
      test.skip(true, 'no jobs available to open detail view');
    }
    await firstCard.click();

    const bar = page.locator('[data-testid="commandbar"]');
    await expect(bar).toBeVisible();

    // Command deck now uses the shared <app-cli-model-selector> chip;
    // open the popover to see the CLI pills.
    await bar.getByTestId('commandbar-agent').click();
    const picker = page.getByTestId('commandbar-agent-picker');
    await expect(picker).toBeVisible();

    const pills = picker.getByTestId('commandbar-agent-picker-cli-pills').getByRole('radio');
    await expect(pills).toHaveCount(4);

    const labels = ['Copilot', 'Claude Code', 'Codex', 'Gemini'] as const;
    const expectedIcons = [ICONS.copilot, ICONS.claude, ICONS.codex, ICONS.gemini];

    const found = new Set<string>();
    for (let i = 0; i < 4; i++) {
      const text = (await pills.nth(i).textContent())?.trim() ?? '';
      const matchedLabel = labels.find(l => text.includes(l));
      expect(matchedLabel, `pill ${i} text "${text}" should contain a known label`).toBeTruthy();
      const matchedIcon = expectedIcons.find(g => text.includes(g));
      expect(matchedIcon, `pill ${i} text "${text}" should contain a known icon`).toBeTruthy();
      found.add(matchedIcon!);
    }
    expect(found.size, 'all four icons should be distinct').toBe(4);
  });

  test('Add Task dialog picker renders distinct icons for each CLI', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    // Create dialog now uses the shared <app-cli-model-selector> chip;
    // open the popover to expose the CLI pills.
    await page.getByTestId('create-agent').click();
    const picker = page.getByTestId('create-agent-picker');
    await expect(picker).toBeVisible();

    const pills = picker.getByTestId('create-agent-picker-cli-pills').getByRole('radio');
    await expect(pills).toHaveCount(4);

    const expectedIcons = [ICONS.copilot, ICONS.claude, ICONS.codex, ICONS.gemini];
    const found = new Set<string>();
    for (let i = 0; i < 4; i++) {
      const text = (await pills.nth(i).textContent())?.trim() ?? '';
      const matchedIcon = expectedIcons.find(g => text.includes(g));
      expect(matchedIcon, `pill ${i} text "${text}" should contain a known icon`).toBeTruthy();
      found.add(matchedIcon!);
    }
    expect(found.size).toBe(4);
  });

  test('job preview cards use a CLI-specific icon when cliType is set', async ({ page }) => {
    await page.goto('/');
    const cards = page.locator('[data-testid="job-card"]');
    const count = await cards.count();
    if (count === 0) test.skip(true, 'no job cards on board');

    // Each card's agent label must show one of the known glyphs OR the
    // generic 🤖 fallback (when cliType is null on disk).
    const allowed = [...Object.values(ICONS), '🤖'];
    for (let i = 0; i < count; i++) {
      const text = (await cards.nth(i).locator('.job-card__agent').textContent())?.trim() ?? '';
      const matched = allowed.find(g => text.includes(g));
      expect(matched, `card ${i} agent line "${text}" should start with a known glyph`).toBeTruthy();
    }
  });
});
