import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

/**
 * Verifies the Add Task dialog and that model catalogs reach the UI.
 * We probe the backend catalog first so a failure fingerprints
 * the right layer.
 */

interface ModelCatalog {
  models: Array<{ id: string; label: string; vendor?: string; isDefault?: boolean }>;
  source: string;
}

test.describe('Add Task — model selection', () => {
  test('backend exposes a non-empty Claude model catalog', async () => {
    const cat = await api<ModelCatalog>('/api/cli/claude/models');
    expect(Array.isArray(cat.models)).toBe(true);
    expect(cat.models.length).toBeGreaterThan(0);
    expect(cat.models.map(m => m.id)).toEqual(
      expect.arrayContaining(['claude-opus-4-7', 'claude-sonnet-4-6'])
    );
  });

  test('Copilot model catalog endpoint never returns 500 (PTY discovery is racy)', async ({ request }) => {
    // Regression guard: when the Copilot /model picker doesn't appear in time,
    // CopilotModelDiscovery used to throw and bubble a 500 to the UI. The
    // endpoint now falls back to disk cache or returns 503 with a body —
    // anything but 5xx-without-context.
    const res = await request.get(`${BACKEND}/api/cli/copilot/models?refresh=true`);
    expect([200, 503]).toContain(res.status());
    const body = await res.json();
    if (res.status() === 200) {
      expect(Array.isArray(body.models)).toBe(true);
    } else {
      expect(body.error, 'error body must explain the failure').toBeTruthy();
    }
  });

  test('Codex model catalog comes from CLI discovery when available', async ({ request }) => {
    const res = await request.get(`${BACKEND}/api/cli/codex/models?refresh=true`);
    expect([200, 503]).toContain(res.status());
    const body = await res.json();
    if (res.status() === 200) {
      expect(Array.isArray(body.models)).toBe(true);
      expect(body.models.length).toBeGreaterThan(0);
      expect(body.source).not.toBe('hardcoded');
    } else {
      expect(body.error, 'error body must explain the failure').toBeTruthy();
    }
  });

  test('Add Task button opens the dialog', async ({ page }) => {
    await page.goto('/');
    const addBtn = page.getByRole('button', { name: /add task/i }).first();
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    // Dialog/modal should appear. Match by role first, fall back to text.
    const dialog = page.getByRole('dialog');
    await expect(dialog.or(page.getByText(/title|titel/i)).first()).toBeVisible();
  });
});

test.describe('Add Task — default model pre-selection', () => {
  test('switching to Claude pre-selects the default model in the dropdown', async ({ page }) => {
    const cat = await api<ModelCatalog>('/api/cli/claude/models');
    const defaultModel = cat.models.find(m => m.isDefault);
    test.skip(!defaultModel, 'no default model in Claude catalog');

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    // Click the Claude CLI button
    await page.getByRole('button', { name: /claude code/i }).click();

    // Wait for the model dropdown to reflect the default
    const modelSelect = page.locator('select').filter({ hasText: /default/i });
    await expect(modelSelect).toBeVisible();

    // The selected option should be the default model id (not the empty "(default — CLI chooses)" option)
    await expect(modelSelect).toHaveValue(defaultModel!.id);
  });
});

test.describe('Add Task — project default', () => {
  test('defaults to the single active project filter', async ({ page }) => {
    const watchPaths = await (await page.request.get(`${BACKEND}/api/watch-paths`)).json();
    test.skip(!Array.isArray(watchPaths) || watchPaths.length < 2, 'needs at least two watch paths');
    const secondName = watchPaths[1].name;

    await page.addInitScript(() => {
      localStorage.removeItem('lastCreateWatchPath');
      localStorage.setItem('activeProjects', '[]');
    });
    await page.goto('/');

    const secondChip = page.getByTestId(`project-filter-${secondName}`);
    await expect(secondChip).toBeVisible();
    await secondChip.click();
    await expect(secondChip).toHaveClass(/filter-chip--active/);

    await page.getByRole('button', { name: /add task/i }).first().click();
    const select = page.getByTestId('create-project-select');
    await expect(select).toBeVisible();
    const selectedOptionText = await select.locator('option:checked').innerText();
    expect(selectedOptionText.trim()).toBe(secondName);
  });

  test('with multiple active projects, uses lastCreateWatchPath', async ({ page }) => {
    // Pre-seed: pretend the user last created in the second project, and activate both.
    const watchPaths = await (await page.request.get(`${BACKEND}/api/watch-paths`)).json();
    test.skip(!Array.isArray(watchPaths) || watchPaths.length < 2, 'needs at least two watch paths');
    const names = watchPaths.map((wp: { name: string }) => wp.name);
    const lastPath = watchPaths[1].path;
    const lastName = watchPaths[1].name;

    await page.addInitScript(([active, last]) => {
      localStorage.setItem('activeProjects', JSON.stringify(active));
      localStorage.setItem('lastCreateWatchPath', last as string);
    }, [names, lastPath]);
    await page.goto('/');

    await page.getByRole('button', { name: /add task/i }).first().click();
    const select = page.getByTestId('create-project-select');
    await expect(select).toBeVisible();
    const selectedOptionText = await select.locator('option:checked').innerText();
    expect(selectedOptionText.trim()).toBe(lastName);
  });
});
