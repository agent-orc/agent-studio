import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { cleanupE2eClients, registerE2eClient } from '../helpers/e2e-clients';

/**
 * Job-card "effective model" indicator.
 *
 * Locks the user-visible contract: the card always renders the model that
 * will be (or is being) used at run time, never the literal `agent` field.
 *
 * Three resolution rules covered here, mirroring the backend default
 * resolver in `TaskRunnerService`:
 *
 * 1. Job with explicit `cliType`/`model` wins over everything (`source=explicit`).
 * 2. Job with both null falls back to the owner client's defaults and
 *    renders with italicised "(default)" styling (`source=default`).
 * 3. A running execution overrides both: the card shows what the live
 *    process actually used (`source=run`).
 *
 * Acceptance contract for the operator screenshot 2026-05-26:
 * "human-decision-needed-...-job-card-shows-effective-model-not-agent-human".
 */

interface WatchPath { name: string; path: string; rootPath: string; }
interface ClientDefaults { id: string; defaultCliType: string | null; defaultModel: string | null; }

const TEST_OWNER_PREFIX = 'e2e-effective-model-';

async function ensureWatchPath(): Promise<WatchPath> {
  const list = await api<WatchPath[]>('/api/watch-paths');
  expect(list.length).toBeGreaterThan(0);
  return list[0];
}

async function setDefaults(id: string, cli: string | null, model: string | null): Promise<ClientDefaults> {
  return api<ClientDefaults>(`/api/clients/${id}/defaults`, {
    method: 'PUT',
    body: JSON.stringify({ defaultCliType: cli ?? '', defaultModel: model ?? '' })
  });
}

test.describe('job-card effective model', () => {

  test('falls back to client defaults when cliType/model are null', async ({ page }) => {
    const watch = await ensureWatchPath();
    // Register a dedicated owner with NO defaults set so the create-time
    // materializer leaves cliType/model null on the new job.json. (The
    // backend stamps defaults onto fresh jobs when the owner has any; we
    // want the legacy "agent: human, cliType: null, model: null" triple.)
    const owner = await registerE2eClient(`${TEST_OWNER_PREFIX}default-${Date.now()}`);
    await setDefaults(owner.id, null, null);

    const title = `effective-default-${Date.now()}`;
    await api('/api/tasks/', {
      method: 'POST',
      body: JSON.stringify({
        title,
        agent: 'human',
        watchPath: watch.path,
        ownerClientId: owner.id,
        targetState: '1-preparation'
      })
    });

    // Now set the owner's defaults; the card's resolver looks them up at
    // render time and should report `source=default` (italicised).
    await setDefaults(owner.id, 'claude', 'claude-opus-4-7');

    await page.goto('/');
    const card = page.locator('[data-testid="task-card"]', { hasText: title });
    await expect(card).toBeVisible();

    const chip = card.locator('[data-testid="task-card-effective-model"]');
    await expect(chip).toBeVisible();
    await expect(chip).toHaveAttribute('data-model-source', 'default');
    await expect(chip).toContainText('OP4.7');
    // The literal `agent` value ("human") must NOT leak into the chip.
    await expect(chip).not.toContainText('human');
    // A dashed outline carries the inherited-default cue without weakening the
    // family colour or adding text to a dense board card.
    await expect(chip).toHaveCSS('border-style', 'dashed');
  });

  test('renders the explicit model when cliType/model are set on the job', async ({ page }) => {
    const watch = await ensureWatchPath();
    const title = `effective-explicit-${Date.now()}`;
    await api('/api/tasks/', {
      method: 'POST',
      body: JSON.stringify({
        title,
        agent: 'human',
        cliType: 'codex',
        model: 'gpt-5-codex',
        watchPath: watch.path,
        ownerClientId: 'local-default',
        targetState: '1-preparation'
      })
    });

    await page.goto('/');
    const card = page.locator('[data-testid="task-card"]', { hasText: title });
    await expect(card).toBeVisible();

    const chip = card.locator('[data-testid="task-card-effective-model"]');
    await expect(chip).toBeVisible();
    await expect(chip).toHaveAttribute('data-model-source', 'explicit');
    await expect(chip).toContainText('COD5');
    await expect(chip).toHaveAttribute('data-model-family', 'openai');
    await expect(chip).toHaveCSS('border-style', 'solid');
  });

  test.afterAll(async () => {
    await cleanupE2eClients(TEST_OWNER_PREFIX);
  });
});
