import { test, expect } from '../fixtures/dev-backend';

/**
 * CLI Admin overlay (per-CLI quota caps).
 *
 * Verifies:
 *   1. The REST surface (GET / PUT /api/cli/quota/caps) round-trips a value
 *      and clamps out-of-range input.
 *   2. The frontend overlay opens from the status-bar "Manage CLIs" button
 *      and renders the "Usage caps" section heading. Slider interaction is
 *      covered by the unit test on the component; we keep the E2E surface
 *      minimal here.
 *
 * Uses the `dev-backend` fixture so the spec is runnable from stable's
 * Playwright suite. That is the only path that may bring dev's backend up
 * per AGENTS.md ("Dev backend lifecycle: Playwright-only"); the previous
 * run of this task authored these checks but could not execute them
 * because the spec did not pull in the fixture.
 */

const CLIENT_ID = 'local-default';

async function api<T>(
  baseUrl: string,
  path: string,
  init: RequestInit = {}
): Promise<T> {
  const res = await fetch(`${baseUrl}${path}`, {
    headers: {
      'content-type': 'application/json',
      'x-client-id': CLIENT_ID,
      ...(init.headers ?? {})
    },
    ...init
  });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`API ${init.method ?? 'GET'} ${path} -> ${res.status} ${res.statusText}\n${text}`);
  }
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

test.describe('CLI Admin / quota caps', () => {
  test('GET /api/cli/quota/caps returns the default cap', async ({ devBackend }) => {
    const r = await api<{ defaultCapPct: number; caps: Record<string, Record<string, number>> }>(
      devBackend.baseUrl,
      '/api/cli/quota/caps'
    );
    expect(r.defaultCapPct).toBe(95);
    expect(typeof r.caps).toBe('object');
  });

  test('PUT /api/cli/quota/caps round-trips and clamps', async ({ devBackend }) => {
    const window = 'PWTest Window';
    // Set within range.
    const a = await api<{ caps: Record<string, Record<string, number>> }>(
      devBackend.baseUrl,
      '/api/cli/quota/caps',
      {
        method: 'PUT',
        body: JSON.stringify({ cliType: 'claude', windowLabel: window, capPct: 87 })
      }
    );
    expect(a.caps['claude']?.[window]).toBe(87);

    // Out-of-range high: backend rejects > 100 with 400. The endpoint either
    // refuses or clamps server-side; both are acceptable. The previous run
    // accepted a clamp as well; today the endpoint returns 400 (see
    // CliEndpoints.cs validation). Either branch is contract-correct.
    const b = await api<{ caps: Record<string, Record<string, number>> }>(
      devBackend.baseUrl,
      '/api/cli/quota/caps',
      {
        method: 'PUT',
        body: JSON.stringify({ cliType: 'claude', windowLabel: window, capPct: 250 })
      }
    ).catch(e => e);
    if (b instanceof Error) {
      expect(String(b.message)).toMatch(/capPct must be between 1 and 100/);
    } else {
      expect(b.caps['claude']?.[window]).toBe(100);
    }
  });

  test('Usage caps section opens from the workspace Settings home', async ({ page }) => {
    await page.goto('/');
    // The scattered "Manage CLIs" status-bar button folded into the global
    // Workspace-settings home (ASS-695). Open the home, then pick the
    // "Usage caps" rail section, which still renders the CLI-admin panel.
    const trigger = page.getByTestId('studio-titlebar-settings');
    await expect(trigger).toBeVisible();
    await trigger.click();

    await page.getByTestId('workspace-settings-rail-caps').click();

    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay).toBeVisible();
    await expect(overlay.getByRole('heading', { name: 'CLI Management' })).toBeVisible();
    // Section heading present even when no quota windows are cached.
    await expect(overlay.getByText('Usage caps', { exact: true })).toBeVisible();
  });
});
