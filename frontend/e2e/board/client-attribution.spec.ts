import { test, expect } from '@playwright/test';
import { api, BACKEND, purgeE2eClients } from '../helpers/api';

/**
 * Client identity + per-task attribution.
 *
 * Locks the user-visible contract for the registration boundary:
 * - the bootstrap `local-default` identity exists on every fresh backend;
 * - registering a new identity returns a kebab-case id;
 * - mutations from an unknown X-Client-Id are rejected with 401;
 * - a job created with `ownerClientId` renders an owner chip on its card;
 * - the Owner filter (studio-shell "Filters" rail) narrows the board to a
 *   single client. The legacy top-bar `client-filter-select` dropdown lives
 *   only in the pre-studio-shell layout (vsCodeLayout=0); the shipping default
 *   layout exposes the same filter as an owner radio list in the activity-bar.
 */

interface ClientSummary {
  id: string;
  displayName: string;
  emoji: string | null;
  colour: string | null;
  kind: string;
}

interface WatchPath { name: string; path: string; rootPath: string; }

const TEST_PREFIX = 'e2e-owner-';
const JOB_TITLE_PREFIX = 'Owner-Chip-';

// Jobs created by the board test, tracked so afterAll can delete them.
// These are real (non-fixture) jobs so they render on the board; they must
// not be left behind on the shared dev workspace.
const createdJobs: Array<{ id: string; watchPath: string }> = [];

interface TaskRow { id: string; title: string; watchPath: string; }

async function ensureWatchPath(): Promise<WatchPath> {
  const list = await api<WatchPath[]>('/api/watch-paths');
  expect(list.length).toBeGreaterThan(0);
  return list[0];
}

async function registerClient(displayName: string, emoji: string, colour: string): Promise<ClientSummary> {
  return api<ClientSummary>('/api/clients/register', {
    method: 'POST',
    body: JSON.stringify({ displayName, emoji, colour, kind: 'human' })
  });
}

test.describe('Client identity + attribution', () => {

  test('bootstrap local-default identity exists', async () => {
    const all = await api<ClientSummary[]>('/api/clients/');
    expect(all.find(c => c.id === 'local-default')).toBeTruthy();
  });

  test('mutations without X-Client-Id are rejected with 401', async () => {
    const res = await fetch(`${BACKEND}/api/tasks`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' }, // no X-Client-Id
      body: JSON.stringify({ title: 'no-header', agent: 'copilot', watchPath: '' })
    });
    expect(res.status).toBe(401);
    const body = await res.json();
    expect(body.error).toBe('client-unknown');
  });

  test('mutations with unknown X-Client-Id are rejected with 401', async () => {
    const res = await fetch(`${BACKEND}/api/tasks`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-client-id': 'ghost-' + Date.now() },
      body: JSON.stringify({ title: 'ghost', agent: 'copilot', watchPath: '' })
    });
    expect(res.status).toBe(401);
  });

  test('registration returns a kebab-case id and is idempotent on displayName', async () => {
    const a = await registerClient(TEST_PREFIX + 'Alpha', '🦊', '#7c3aed');
    expect(a.id).toBe(TEST_PREFIX + 'alpha');
    const b = await registerClient(TEST_PREFIX + 'Alpha', '🐺', '#16a34a');
    expect(b.id).toBe(a.id);
    expect(b.emoji).toBe('🐺'); // refresh accepted
  });

  test('owner chip and client filter on the board', async ({ page }) => {
    const watch = await ensureWatchPath();
    const ownerA = await registerClient(TEST_PREFIX + 'Card A Owner', '🦊', '#7c3aed');
    const ownerB = await registerClient(TEST_PREFIX + 'Card B Owner', '🐢', '#16a34a');

    // Two jobs, one per owner. Land in 1-preparation (default) so they appear
    // immediately on the board without triggering a runner pickup.
    const titleA = `Owner-Chip-A-${Date.now()}`;
    const titleB = `Owner-Chip-B-${Date.now()}`;
    const jobA = await api<{ id: string }>('/api/tasks/', {
      method: 'POST',
      body: JSON.stringify({
        title: titleA,
        agent: 'copilot',
        watchPath: watch.path,
        ownerClientId: ownerA.id,
        targetState: '1-preparation'
      })
    });
    createdJobs.push({ id: jobA.id, watchPath: watch.path });
    const jobB = await api<{ id: string }>('/api/tasks/', {
      method: 'POST',
      body: JSON.stringify({
        title: titleB,
        agent: 'copilot',
        watchPath: watch.path,
        ownerClientId: ownerB.id,
        targetState: '1-preparation'
      })
    });
    createdJobs.push({ id: jobB.id, watchPath: watch.path });

    await page.goto('/');

    // Both owner chips render on their respective cards.
    const cardA = page.locator('[data-testid="task-card"]', { hasText: titleA });
    const cardB = page.locator('[data-testid="task-card"]', { hasText: titleB });
    await expect(cardA).toBeVisible();
    await expect(cardB).toBeVisible();

    const chipA = cardA.locator('[data-testid="task-card-owner"]');
    const chipB = cardB.locator('[data-testid="task-card-owner"]');
    await expect(chipA).toBeVisible();
    await expect(chipB).toBeVisible();
    await expect(chipA).toContainText(ownerA.displayName);
    await expect(chipB).toContainText(ownerB.displayName);
    await expect(chipA).toHaveAttribute('data-owner-id', ownerA.id);
    await expect(chipB).toHaveAttribute('data-owner-id', ownerB.id);

    // Screenshot evidence: two cards owned by different clients.
    await page.screenshot({ path: 'test-results/client-attribution-two-owners.png', fullPage: true });

    // Filter narrows the board to the chosen client. In the shipping
    // studio-shell layout the owner filter is a radio list inside the
    // activity-bar "Filters" rail, so open that rail first.
    await page.getByTestId('studio-ab-filters').click();
    const ownerRadioA = page.getByTestId(`kanban-filter-owner-${ownerA.id}`);
    const ownerRadioB = page.getByTestId(`kanban-filter-owner-${ownerB.id}`);
    const ownerRadioAll = page.getByTestId('kanban-filter-owner-all');
    await expect(ownerRadioA).toBeVisible();

    await ownerRadioA.click();
    await expect(cardA).toBeVisible();
    await expect(cardB).toHaveCount(0);

    await ownerRadioB.click();
    await expect(cardB).toBeVisible();
    await expect(cardA).toHaveCount(0);

    await ownerRadioAll.click();
    await expect(cardA).toBeVisible();
    await expect(cardB).toBeVisible();
  });

  test.afterAll(async () => {
    // 1. Delete the jobs this spec planted. Delete the tracked ids first,
    //    then sweep the board by title prefix in case a created job was not
    //    captured (e.g. a partial run). These are real jobs on the shared
    //    dev workspace, so leaving them behind would pollute the board.
    const toDelete = new Map<string, { id: string; watchPath: string }>();
    for (const j of createdJobs) toDelete.set(`${j.watchPath}::${j.id}`, j);
    try {
      const rows = await api<TaskRow[]>('/api/tasks/?includeFixtures=true');
      for (const r of rows) {
        if (r.title?.startsWith(JOB_TITLE_PREFIX)) {
          toDelete.set(`${r.watchPath}::${r.id}`, { id: r.id, watchPath: r.watchPath });
        }
      }
    } catch { /* ignore: tracked ids are still deleted below */ }
    for (const j of toDelete.values()) {
      try {
        await api(`/api/tasks/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(j.watchPath)}`, { method: 'DELETE' });
      } catch { /* ignore */ }
    }

    // 2. Cleanup of the e2e-owner clients: retire, then permanently purge
    //    (AGT-2748) so leftovers stop accumulating in the shared dev
    //    workspace across runs. Registration is idempotent on displayName,
    //    so a re-run simply re-creates the identity if the spec needs it again.
    await purgeE2eClients(TEST_PREFIX);
  });
});
