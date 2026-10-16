import { expect, test } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog } from '../helpers/theme';

const projectName = 'Product Fixture';
const projectId = 'PROJ-996';
const taskId = 'product-change';
const stageAddress = 'http://localhost:4219/';
const screenshot = `<svg xmlns="http://www.w3.org/2000/svg" width="800" height="450"><rect width="800" height="450" fill="#f3f4f6"/><text x="100" y="130" font-size="40">Product heading</text></svg>`;

test('creates a confirmed change, marks delivery feedback, and accepts at phone width', async ({ page, devBackend }) => {
  void devBackend;
  let createdPrompt = '';
  let feedbackBody = '';
  let updatedPreview = false;
  let accepted = false;
  const url = { id: 'stage', label: 'Stage', url: stageAddress, sortOrder: 0, startRule: null };
  const project = {
    sourceType: 'local-folder', id: projectId, displayName: projectName, shortCode: 'PRD',
    workspaceId: 'ws-product', storageLocation: '/mock/tasks/product', rootPath: '/mock/repo/product',
    repositoryPath: '/mock/repo/product', repositoryUrl: null, sortOrder: 0, archived: false,
    color: null, cliDefault: null, modelDefault: null, createdAt: '2026-10-16T10:00:00Z', urls: [url],
  };
  const card = {
    id: taskId, taskKey: `${projectId}::${taskId}`, key: 'PRD-1', title: 'Improve the heading',
    state: '5-human-review', order: 0, agent: 'codex', createdAt: '2026-10-16T10:00:00Z',
    watchPath: '/mock/tasks/product', projectName, folderPath: '/mock/tasks/product/5-human-review/product-change',
    lastActivity: '2026-10-16T10:00:00Z', enteredLaneAt: '2026-10-16T10:00:00Z',
    sessionName: null, model: null, cliType: null, useOwnSession: null,
    lastUsage: null, execution: null, commit: null,
  };

  await page.route('http://localhost:4219/**', route => route.fulfill({
    status: 200, contentType: 'text/html', body: '<main><h1>Product heading</h1></main>',
  }));
  await page.route('**/api/**', async route => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({
      status, contentType: 'application/json', body: JSON.stringify(body),
    });
    if (pathname === '/api/v1/studio/auth/status') return json({
      profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
    });
    if (pathname === '/api/watch-paths') return json([{ name: projectName, path: project.storageLocation, rootPath: project.rootPath }]);
    if (pathname === '/api/v1/workspaces') return json([{
      id: 'ws-product', displayName: 'Product', sortOrder: 0, isDefault: true,
      color: null, createdAt: '2026-10-16T10:00:00Z', projects: [project],
    }]);
    if (pathname === '/api/v1/projects') return json([project]);
    if (pathname === '/api/tasks' && request.method() === 'POST') {
      createdPrompt = String(request.postDataJSON()?.promptMarkdown ?? '');
      return json({ id: 'new-change' });
    }
    if (pathname === '/api/tasks' || pathname === '/api/tags') return json([]);
    if (pathname === '/api/v1/studio/board') return json({
      backlog: [], preparation: [], ready: [], progress: [], autoReview: [],
      humanReview: [card], completed: [], archive: [],
    });
    if (pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (pathname.endsWith('/urls/stage/readiness')) return json({
      kind: 'healthy', statusCode: 200, framePolicy: 'allowed', detail: null, durationMs: 1,
    });
    if (pathname.endsWith('/urls/stage/process')) return route.fulfill({ status: 204 });
    if (pathname.endsWith('/urls/stage/context')) return json({
      projectName, repositoryName: projectName, workingDirectory: project.rootPath,
      repoRoot: project.rootPath, isRepo: true, branch: 'develop',
      headSha: 'abcdef0123456789abcdef0123456789abcdef01', headShortSha: 'abcdef01',
      comparisonRef: 'origin/develop', comparisonKind: 'integration', ahead: 0, behind: 0,
      isDirty: false, error: null,
    });
    if (pathname.endsWith(`/${taskId}/screenshots`)) {
      const version = updatedPreview ? 2 : 1;
      return json({ jobId: taskId, screenshots: [{
      jobId: taskId, jobTitle: card.title, projectName, watchPath: card.watchPath,
      fileName: `after-${version}.svg`, relativePath: `results/after-${version}.svg`,
      url: `/api/tasks/${taskId}/screenshot?path=results%2Fafter-${version}.svg`,
      caption: 'After: home page', status: 'passed', source: 'mocked', compositeParts: [],
      localPath: `/mock/results/after-${version}.svg`, timestampUtc: '2026-10-16T10:00:00Z',
    }] });
    }
    if (pathname.endsWith(`/${taskId}/screenshot`)) return route.fulfill({
      status: 200, contentType: 'image/svg+xml', body: screenshot,
    });
    if (pathname.endsWith(`/${taskId}/preview-feedback`)) {
      feedbackBody = request.postDataBuffer()?.toString('utf8') ?? '';
      return json({ status: 'queued' }, 202);
    }
    if (pathname.endsWith(`/${taskId}/move`)) {
      accepted = request.postDataJSON()?.targetState === '6-completed';
      return json({});
    }
    return route.continue();
  });

  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/#');
  await dismissDevErrorDialog(page);
  const projectRow = page.getByTestId(`studio-explorer-project-${projectName}`);
  await expect(projectRow).toBeVisible();
  const urlRow = page.getByTestId(`studio-explorer-project-url-${projectName}-stage`);
  if (!await urlRow.isVisible().catch(() => false)) await projectRow.click();
  await urlRow.click();
  await expect(page.getByTestId('url-preview-frame')).toBeVisible();
  await page.getByTestId('product-start-change').click();
  await page.getByTestId('product-wish').fill('Make the heading clearer');
  await page.getByTestId('product-draft').getByRole('button', { name: 'Continue' }).click();
  await page.getByTestId('product-location').fill('the home page hero');
  await page.getByTestId('product-draft').getByRole('button', { name: 'Continue' }).click();
  await page.getByTestId('product-example').fill('Use the pricing page as a reference');
  await page.getByRole('button', { name: 'Review change' }).click();
  await expect(page.getByTestId('product-proposal')).toContainText('20,000 to 60,000 tokens');
  expect(createdPrompt).toBe('');
  await page.getByTestId('product-confirm-change').click();
  await expect.poll(() => createdPrompt).toContain('before and after');
  expect(createdPrompt).toContain(stageAddress);

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByTestId('studio-sidebar')).toHaveCount(0);
  await page.getByTestId('product-card-select').selectOption(taskId);
  await expect(page.getByTestId('product-status')).toContainText('Ready to look at');
  const image = page.getByTestId('product-markup-image');
  await expect(image).toBeVisible();
  await image.scrollIntoViewIfNeeded();
  const bounds = await image.boundingBox();
  if (!bounds) throw new Error('Delivery screenshot is not visible.');
  await page.mouse.move(bounds.x + 20, bounds.y + 20);
  await page.mouse.down();
  await page.mouse.move(bounds.x + 90, bounds.y + 80);
  await page.mouse.up();
  await expect(page.getByTestId('product-selection')).toBeVisible();
  await page.getByTestId('product-comment').fill('Increase the heading contrast');
  await page.getByText('Element details, if known').click();
  await page.getByTestId('product-selector').fill('main h1');
  await page.getByTestId('product-send-feedback').click();
  await expect.poll(() => feedbackBody).toContain('Increase the heading contrast');
  expect(feedbackBody).toContain('main h1');
  expect(feedbackBody).toContain(stageAddress);
  await expect(page.getByTestId('product-accept')).toHaveCount(0);
  await page.getByTestId('product-check-update').click();
  await expect(page.getByTestId('product-accept')).toHaveCount(0);
  updatedPreview = true;
  await page.getByTestId('product-check-update').click();
  await expect(page.getByTestId('product-accept')).toBeVisible();
  const resultDir = process.env.JOB_RESULTS_DIR;
  if (resultDir) {
    mkdirSync(resultDir, { recursive: true });
    await page.screenshot({ path: join(resultDir, 'product-preview-phone--mocked.png'), fullPage: true });
  }
  await page.getByTestId('product-accept').click();
  await expect.poll(() => accepted).toBe(true);
});
