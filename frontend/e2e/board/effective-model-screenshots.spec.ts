import { test, expect } from '@playwright/test';
import { api, purgeE2eClients } from '../helpers/api';
import path from 'node:path';

/**
 * Visual-evidence companion to `effective-model-on-card.spec.ts`.
 * Captures the three states (default, explicit, plus a combined board view)
 * for the human-decision job folder so a reviewer can see the rendered
 * change without firing up Playwright.
 */

interface WatchPath { name: string; path: string; rootPath: string; }
interface ClientSummary { id: string; displayName: string; defaultCliType: string | null; defaultModel: string | null; kind: string; }

const TEST_OWNER_PREFIX = 'e2e-effective-model-shot-';

// Resolve evidence folder so the screenshots survive past the next
// Playwright run. Mirrors the contract in protocol-style.md.
const EVIDENCE_DIR = process.env.EFFECTIVE_MODEL_EVIDENCE_DIR
  ?? path.join('test-results', 'effective-model-screenshots');

async function ensureWatchPath(): Promise<WatchPath> {
  const list = await api<WatchPath[]>('/api/watch-paths');
  expect(list.length).toBeGreaterThan(0);
  return list[0];
}

async function registerOwner(displayName: string): Promise<ClientSummary> {
  return api<ClientSummary>('/api/clients/register', {
    method: 'POST',
    body: JSON.stringify({ displayName, emoji: '🧪', colour: '#7c3aed', kind: 'human' })
  });
}

async function setDefaults(id: string, cli: string | null, model: string | null): Promise<void> {
  await api(`/api/clients/${id}/defaults`, {
    method: 'PUT',
    body: JSON.stringify({ defaultCliType: cli ?? '', defaultModel: model ?? '' })
  });
}

test.describe('effective model screenshots', () => {
  test('captures default + explicit cards side by side', async ({ page }) => {
    const watch = await ensureWatchPath();

    const owner = await registerOwner(`${TEST_OWNER_PREFIX}${Date.now()}`);
    await setDefaults(owner.id, null, null);

    const defaultTitle = `effective-model-DEFAULT-${Date.now()}`;
    await api('/api/tasks/', {
      method: 'POST',
      body: JSON.stringify({
        title: defaultTitle,
        agent: 'human',
        watchPath: watch.path,
        ownerClientId: owner.id,
        targetState: '1-preparation'
      })
    });

    const explicitTitle = `effective-model-EXPLICIT-${Date.now()}`;
    await api('/api/tasks/', {
      method: 'POST',
      body: JSON.stringify({
        title: explicitTitle,
        agent: 'human',
        cliType: 'codex',
        model: 'gpt-5-codex',
        watchPath: watch.path,
        ownerClientId: owner.id,
        targetState: '1-preparation'
      })
    });

    await setDefaults(owner.id, 'claude', 'claude-opus-4-7');

    await page.goto('/');
    const defaultCard = page.locator('[data-testid="task-card"]', { hasText: defaultTitle });
    const explicitCard = page.locator('[data-testid="task-card"]', { hasText: explicitTitle });
    await expect(defaultCard).toBeVisible();
    await expect(explicitCard).toBeVisible();

    await defaultCard.screenshot({ path: path.join(EVIDENCE_DIR, 'card-default-italic.png') });
    await explicitCard.screenshot({ path: path.join(EVIDENCE_DIR, 'card-explicit.png') });
  });

  test.afterAll(async () => {
    // Retire, then permanently purge (AGT-2748) so leftovers stop
    // accumulating in the shared dev workspace across runs.
    await purgeE2eClients(TEST_OWNER_PREFIX);
  });
});
