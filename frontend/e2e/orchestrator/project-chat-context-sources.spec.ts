import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { installOrchestratorChatBootstrap } from '../helpers/orchestrator-chat-bootstrap';
import { setTheme } from '../helpers/theme';

const PROJECT = 'context-fixture';
const RESULTS = resolve(process.env.JOB_RESULTS_DIR ?? resolve(process.cwd(), '..', 'results', 'AGT-2573'));
mkdirSync(RESULTS, { recursive: true });

interface ContextReference {
  kind: string;
  reference: string;
  projectId: string;
}

interface CapturedSend {
  contextEnvelope?: { explicitReferences?: ContextReference[]; capturedAt?: string };
}

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installContextFixtures(page: Page) {
  await installOrchestratorChatBootstrap(page, PROJECT);
  const captured: CapturedSend[] = [];
  const turns: any[] = [];

  await page.route('**/api/orchestrator/sessions', route => json(route, {
    sessions: [{
      contextKey: `project:${PROJECT}`,
      kind: 'project', projectId: PROJECT, taskKey: null,
      createdAt: '2026-08-10T08:00:00Z', updatedAt: '2026-08-10T10:00:00Z',
      model: 'gpt-5.4-mini', cumulativeInputTokens: 840, cumulativeOutputTokens: 120,
      cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      summary: 'Review the context workspace sources', runtimeStatus: 'idle', queuePosition: 0,
    }],
  }));
  // Scoped to the single-response route: `**/api/search**` would also swallow
  // the palette's `/api/search/stream`, which speaks server-sent events.
  await page.route('**/api/search?**', route => json(route, {
    tasks: [{ domain: 'tasks', projectName: PROJECT, title: 'Prepare context contracts', subtitle: '2-ready', taskKey: 'CTX-22', lane: '2-ready' }],
    files: [{ domain: 'files', projectName: PROJECT, title: 'context-envelope.ts', subtitle: 'src/context-envelope.ts', path: 'src/context-envelope.ts', isWiki: false }],
    commits: [{ domain: 'commits', projectName: PROJECT, title: 'feat: persist context receipts', subtitle: '9a11cbed', sha: '9a11cbed0123456789abcdef' }],
    errors: {}, durationMs: 3,
  }));
  await page.route('**/api/projects/*/wiki/search**', route => json(route, {
    query: 'context', semanticUsed: false, expandedTerms: [], durationMs: 2,
    results: [{ relPath: 'concepts/context-model.md', title: 'Context model', kind: 'md', snippet: 'Context scopes and receipts', score: 1, updatedAt: null }],
  }));
  await page.route('**/api/projects/*/workbenches', route => json(route, {
    projectName: PROJECT, includesHistory: false, count: 1,
    items: [{
      id: 'context-workbench', key: 'CTX-WB', title: 'Context workbench', summary: 'Inspect source resolution',
      status: 'active', phase: 'testing', updatedAtUtc: '2026-08-10T10:00:00Z',
      entryPath: 'operations/context-workbench/index.html', valid: true, error: null, sourceTaskKeys: [],
    }],
  }));
  await page.route('**/api/runner/**/orchestrator-chat', async route => {
    const request = route.request();
    if (request.method() === 'GET') {
      await json(route, { project: PROJECT, turns });
      return;
    }
    const body = request.postDataJSON() as CapturedSend & { text: string };
    captured.push(body);
    const now = new Date().toISOString();
    const references = body.contextEnvelope?.explicitReferences ?? [];
    turns.push(
      { id: `user-${turns.length}`, ts: now, role: 'user', text: body.text },
      {
        id: `reply-${turns.length}`, ts: now, role: 'orchestrator', text: 'The selected sources were resolved at send time.',
        contextReceipt: {
          scope: 'project', contextKey: `project:${PROJECT}`, taskKey: null,
          includedBlocks: references.map(reference => reference.reference), capturedAt: body.contextEnvelope?.capturedAt ?? now,
          receiptId: 'rcp_context_fixture_001', userTurnId: `user-${turns.length}`,
          budget: { automaticSoftCapTokens: 4000, automaticHardCapTokens: 6000, totalHardCapTokens: 8000, estimatedIncludedTokens: 3720 },
          sources: [
            { sourceId: `digest:${PROJECT}`, kind: 'project-base', revision: '2026-08-10T10:00:00Z', sha256: 'a'.repeat(64), freshness: 'current', includedCharacters: 3200, estimatedTokens: 800, status: 'included' },
            ...references.map((reference, index) => ({
              sourceId: reference.reference,
              kind: reference.kind,
              revision: reference.kind === 'commit' ? '9a11cbed0123456789abcdef' : null,
              sha256: index === 1 ? null : 'b'.repeat(64),
              freshness: index === 1 ? 'unknown' : 'current',
              includedCharacters: index === 1 ? 0 : 4200,
              estimatedTokens: index === 1 ? 0 : 1050,
              status: index === 0 ? 'excerpted' : index === 1 ? 'unresolved' : 'included',
              reason: index === 0 ? 'Bounded to the submitted context budget.' : index === 1 ? 'The referenced repository text does not exist.' : null,
            })),
          ],
        },
      },
    );
    await json(route, { project: PROJECT, reply: turns.at(-1) });
  });
  return captured;
}

async function openChat(page: Page) {
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.getByTestId('orch-side-sheet-toggle').click();
  await expect(page.getByTestId('chat-input')).toBeVisible();
}

test('project chat attaches known sources, inspects the persisted receipt, and keeps one permanent project chat', async ({ page }) => {
  const captured = await installContextFixtures(page);
  await openChat(page);

  // The automatic current-tab chip is the first thing in the composer's chip
  // row, with its own send-time estimate.
  const automaticChip = page.getByTestId('chat-context-attachment-context:automatic');
  await expect(automaticChip).toContainText('Board');
  await expect(automaticChip).toContainText('~1.6k');

  await page.getByTestId('chat-context-attachment-add').click();
  await expect(page.getByTestId('orch-context-current-automatic')).toContainText('already included');
  await page.getByTestId('orch-context-source-search').fill('context');

  await expect(page.getByTestId('orch-context-group-tasks')).toContainText('Prepare context contracts');
  await expect(page.getByTestId('orch-context-group-wiki')).toContainText('Context workbench');
  await expect(page.getByTestId('orch-context-group-files')).toContainText('context-envelope.ts');
  await expect(page.getByTestId('orch-context-group-commits')).toContainText('persist context receipts');

  await page.getByTestId('orch-context-group-wiki').getByRole('button', { name: /Context workbench/ }).click();
  await page.getByTestId('orch-context-group-files').getByRole('button', { name: /context-envelope.ts/ }).click();
  await page.getByTestId('orch-context-group-commits').getByRole('button', { name: /persist context receipts/ }).click();
  // Automatic block plus the three added sources, each carrying its estimate.
  await expect(page.getByTestId('chat-context-attachments').getByRole('listitem')).toHaveCount(4);
  await expect(page.getByTestId('chat-context-attachments')).toContainText('~1.6k');

  await setTheme(page, 'light');
  await page.screenshot({ path: resolve(RESULTS, 'project-chat-context-picker--mocked-light.png'), fullPage: false });
  await page.getByRole('button', { name: 'Close context picker' }).click();

  // Removing a chip drops exactly that source and leaves the automatic block.
  const chipRow = page.getByTestId('chat-context-attachments');
  await chipRow.getByRole('listitem').last().getByRole('button').click();
  await expect(chipRow.getByRole('listitem')).toHaveCount(3);
  await expect(automaticChip).toBeVisible();
  await page.getByTestId('chat-context-attachment-add').click();
  await page.getByTestId('orch-context-group-commits')
    .getByRole('button', { name: /persist context receipts/ }).click();
  await expect(chipRow.getByRole('listitem')).toHaveCount(4);
  await page.getByRole('button', { name: 'Close context picker' }).click();

  await page.getByTestId('chat-input').fill('Compare these context sources.');
  await page.getByTestId('chat-send').click();
  await expect.poll(() => captured.length).toBe(1);
  expect(captured[0].contextEnvelope?.explicitReferences).toEqual([
    { kind: 'page', reference: `page:${PROJECT}/operations/context-workbench/index.html`, projectId: PROJECT },
    { kind: 'repository-file', reference: 'src/context-envelope.ts', projectId: PROJECT },
    { kind: 'commit', reference: `commit:${PROJECT}/9a11cbed0123456789abcdef`, projectId: PROJECT },
  ]);

  await expect(page.getByTestId('orch-context-inspect-toggle')).toContainText('4 sources');
  await page.getByTestId('orch-context-inspect-toggle').click();
  const inspector = page.getByTestId('orch-context-inspector');
  await expect(inspector).toContainText('Excerpted');
  await expect(inspector).toContainText('Unresolved');
  await expect(inspector).toContainText('Included');

  await setTheme(page, 'dark');
  await page.screenshot({ path: resolve(RESULTS, 'project-chat-context-inspector--mocked-dark.png'), fullPage: false });

  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.getByTestId('orch-side-sheet-toggle').click();
  await expect(page.getByTestId('orch-context-inspect-toggle')).toBeVisible();
  await page.getByTestId('orch-context-badge').click();
  await expect(page.getByRole('heading', { name: 'Chat history' })).toBeVisible();
  await expect(page.getByTestId(`chat-switcher-row-project:${PROJECT}`)).toHaveCount(1);
  await expect(page.getByTestId(`chat-switcher-row-project:${PROJECT}`)).toContainText('Review the context workspace sources');
});
