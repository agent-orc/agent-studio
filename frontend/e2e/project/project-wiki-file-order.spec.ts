import { expect, test } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

const RESULTS_DIR = process.env.PROJECT_WIKI_RESULTS_DIR
  ?? process.env.JOB_RESULTS_DIR
  ?? resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-wiki-file-order');

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test('document drag order stays in place, survives reload, and fits the fixed table in both themes', async ({ page, devBackend }) => {
  mkdirSync(RESULTS_DIR, { recursive: true });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      profile: 'local',
      bootstrapRequired: false,
      authenticated: false,
      user: null,
    }),
  }));
  const watchPaths = await fetch(`${devBackend.baseUrl}/api/watch-paths`).then(response => response.json()) as { name: string }[];
  expect(watchPaths.length).toBeGreaterThan(0);
  const projectName = watchPaths[0].name;
  const encodedProject = encodeURIComponent(projectName);
  const folderRel = 'order-demo';
  let orderedNames = ['alpha.md', 'bravo.md', 'charlie.md'];
  let treeReads = 0;
  let folderReads = 0;
  let putBody: unknown = null;
  let releasePut: (() => void) | undefined;
  const putGate = new Promise<void>(resolveGate => { releasePut = resolveGate; });

  const documentNode = (name: string) => ({
    name,
    title: name.replace('.md', '').toUpperCase(),
    relPath: `${folderRel}/${name}`,
    type: 'md',
    children: [],
    metadata: null,
    classification: null,
  });
  const treePayload = () => ({
    projectName,
    baseDir: '/repo/docs',
    exists: true,
    root: [{
      name: folderRel,
      title: 'Order demo',
      relPath: folderRel,
      type: 'folder',
      children: orderedNames.map(documentNode),
      metadata: null,
      classification: null,
    }],
  });
  const folderPayload = () => ({
    path: folderRel,
    name: 'Order demo',
    children: orderedNames.map((name, index) => ({
      name,
      relPath: `${folderRel}/${name}`,
      kind: 'page',
      fileType: 'md',
      title: name.replace('.md', '').toUpperCase(),
      summary: `Stable summary ${index + 1}`,
      updatedAt: '2026-07-22T12:00:00Z',
      size: 1024 + index,
      childCount: null,
      classification: null,
    })),
  });

  await page.route(`**/api/projects/${encodedProject}/wiki/tree`, async route => {
    treeReads++;
    await route.fulfill({ json: treePayload(), headers: { etag: `"tree-${treeReads}"` } });
  });
  await page.route(`**/api/projects/${encodedProject}/wiki/folder/${folderRel}`, async route => {
    folderReads++;
    await route.fulfill({ json: folderPayload() });
  });
  await page.route(`**/api/projects/${encodedProject}/wiki/file-order`, async route => {
    putBody = route.request().postDataJSON();
    orderedNames = (putBody as { orderedNames: string[] }).orderedNames;
    await putGate;
    await route.fulfill({ json: { relPath: 'app/config/wiki-order.json', sha: 'e2e1234' } });
  });

  let navigationCount = 0;
  page.on('framenavigated', frame => {
    if (frame === page.mainFrame()) navigationCount++;
  });
  await page.goto(`/#/projects/${slugFor(projectName)}/wiki?folder=${folderRel}`, {
    waitUntil: 'commit',
  });
  const view = page.getByTestId('wiki-folder-view');
  await expect(view).toBeVisible({ timeout: 30_000 });
  // A dirty task worktree can raise the non-closable crash-recovery prompt.
  // Hide only this browser instance; never mutate the operator's recovery queue.
  const recoveryOverlay = page.getByTestId('crash-recovery-prompt-overlay');
  if (await recoveryOverlay.isVisible().catch(() => false)) {
    await recoveryOverlay.evaluate(element => {
      (element as HTMLElement).style.display = 'none';
      (element as HTMLElement).style.pointerEvents = 'none';
    });
  }
  const navigationCountBeforeDrop = navigationCount;

  const handle = page.getByTestId(`wiki-folder-drag-${folderRel}/charlie.md`);
  const target = page.getByTestId(`wiki-folder-row-${folderRel}/alpha.md`);
  await expect(handle).toBeVisible();
  await handle.dragTo(target);

  const rowIds = () => view.locator('[data-testid^="wiki-folder-row-"]')
    .evaluateAll(rows => rows.map(row => row.getAttribute('data-testid')));
  await expect.poll(rowIds).toEqual([
    `wiki-folder-row-${folderRel}/charlie.md`,
    `wiki-folder-row-${folderRel}/alpha.md`,
    `wiki-folder-row-${folderRel}/bravo.md`,
  ]);
  await expect.poll(() => page.locator(`[data-testid^="project-wiki-file-${folderRel}/"]`)
    .evaluateAll(rows => rows.map(row => row.getAttribute('data-testid')))).toEqual([
      `project-wiki-file-${folderRel}/charlie.md`,
      `project-wiki-file-${folderRel}/alpha.md`,
      `project-wiki-file-${folderRel}/bravo.md`,
    ]);
  expect(putBody).toEqual({ parentRelPath: folderRel, orderedNames });
  expect(navigationCount).toBe(navigationCountBeforeDrop);
  await expect(page.getByTestId('project-wiki-loading')).toHaveCount(0);

  releasePut?.();
  await expect.poll(() => treeReads).toBeGreaterThan(1);
  await expect.poll(() => folderReads).toBeGreaterThan(1);

  const layout = await view.getByTestId('wiki-folder-table').evaluate(table => {
    const titleCell = table.querySelector<HTMLElement>('.wfolder__cell-title');
    const fileCell = table.querySelector<HTMLElement>('.wfolder__cell-file');
    return {
      tableLayout: getComputedStyle(table).tableLayout,
      titleWidth: titleCell?.getBoundingClientRect().width ?? 0,
      fileWidth: fileCell?.getBoundingClientRect().width ?? 0,
    };
  });
  expect(layout.tableLayout).toBe('fixed');
  expect(layout.titleWidth).toBeGreaterThan(100);
  expect(layout.fileWidth).toBeGreaterThan(80);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('project-wiki-section')
      .screenshot({ path: join(RESULTS_DIR, `wiki-file-order-${theme}.png`) });
  }

  await page.reload({ waitUntil: 'commit' });
  await expect(page.getByTestId('wiki-folder-view')).toBeVisible({ timeout: 30_000 });
  await expect.poll(() => page.getByTestId('wiki-folder-view')
    .locator('[data-testid^="wiki-folder-row-"]')
    .evaluateAll(rows => rows.map(row => row.getAttribute('data-testid')))).toEqual([
      `wiki-folder-row-${folderRel}/charlie.md`,
      `wiki-folder-row-${folderRel}/alpha.md`,
      `wiki-folder-row-${folderRel}/bravo.md`,
    ]);
});
