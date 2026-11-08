import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND, DEFAULT_CLIENT_ID } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * A task document image opens in the shared `app-media-lightbox`.
 *
 * The acceptance scenario from the feature task:
 *   1. A task document contains a markdown image.
 *   2. The user opens the image from its rendered preview.
 *   3. An enlarged preview opens, closable via Escape / close button.
 *
 * The Docs preview is available without opening the editor or driving a runner.
 */

interface WatchPath { path: string; name?: string }

const PNG_BYTES = makeTinyPng();

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function uploadAttachment(jobId: string, watchPath: string, fileName: string): Promise<string> {
  const url = `/api/tasks/${encodeURIComponent(jobId)}/attachments?watchPath=${encodeURIComponent(watchPath)}`;
  const formData = new FormData();
  const blob = new Blob([new Uint8Array(PNG_BYTES)], { type: 'image/png' });
  formData.append('file', blob, fileName);
  const res = await fetch(`${BACKEND}${url}`, {
    method: 'POST',
    body: formData,
    headers: { 'X-Client-Id': DEFAULT_CLIENT_ID }
  });
  if (!res.ok) throw new Error(`upload failed: ${res.status} ${await res.text()}`);
  const payload = (await res.json()) as { relativePath: string };
  return payload.relativePath;
}

async function setPrompt(jobId: string, watchPath: string, markdown: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(jobId)}/files/prompt.md?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'PUT',
    body: JSON.stringify({ content: markdown })
  });
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE'
    });
  } catch { /* best-effort */ }
}

async function openDetail(page: Page, id: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
  // Task entry opens Overview; at this viewport Docs is in the tab overflow.
  const promptTabs = page.getByRole('tablist', { name: 'Prompt pane tabs' });
  await promptTabs.locator('..').getByTestId('pane-tabs-overflow').click();
  await page.getByTestId('pane-tabs-overflow-item-description').click();
  await expect(page.getByRole('tabpanel', { name: 'Docs' })).toBeVisible();
}

test.describe('Task description image lightbox', () => {
  test('opening a markdown image in Docs keeps arrow keys in the shared lightbox', async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    const created = await createJob({
      title: `e2e-md-image-lightbox-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      promptMarkdown: '# Placeholder\n',
      targetState: '2-ready'
    });
    const jobId = created.id;

    try {
      // Upload an image and rewrite prompt.md to reference it. We do this
      // through the same APIs the editor uses so the resolved path matches
      // the production rendering pipeline exactly.
      const rel = await uploadAttachment(jobId, watchPath, 'demo.png');
      await setPrompt(jobId, watchPath, `# With image\n\nHere is a screenshot:\n\n![demo screenshot](${rel})\n`);

      await page.setViewportSize({ width: 1400, height: 900 });
      await openDetail(page, jobId, watchPath);

      const img = page.getByRole('button', { name: 'Open image: demo screenshot' });
      await expect(img).toBeVisible({ timeout: 10_000 });

      const promptShot = await page.getByRole('tabpanel', { name: 'Docs' }).screenshot();
      await testInfo.attach('task-description-with-image', { body: promptShot, contentType: 'image/png' });

      // Lightbox should not be in the DOM yet.
      await expect(page.getByTestId('media-lightbox')).toHaveCount(0);

      const detailUrl = page.url();
      await img.click();
      await page.keyboard.press('ArrowRight');

      const lightbox = page.getByTestId('media-lightbox');
      await expect(lightbox).toBeVisible({ timeout: 5_000 });
      await expect(page).toHaveURL(detailUrl);
      const lbImg = page.getByTestId('media-lightbox-image');
      await expect(lbImg).toBeVisible();
      // The lightbox image src should resolve to the same attachment URL.
      const lbSrc = await lbImg.getAttribute('src');
      expect(lbSrc, 'lightbox src should reference the uploaded attachment').toContain(rel);

      const openShot = await page.screenshot({ fullPage: false });
      await testInfo.attach('lightbox-open', { body: openShot, contentType: 'image/png' });

      // Escape closes the lightbox via the modal stack.
      await page.keyboard.press('Escape');
      await expect(lightbox).toBeHidden({ timeout: 2_000 });

      const closedShot = await page.screenshot({ fullPage: false });
      await testInfo.attach('lightbox-closed', { body: closedShot, contentType: 'image/png' });

      // Click-on-image again, then click the explicit close button.
      await img.click();
      await expect(lightbox).toBeVisible();
      await page.getByTestId('media-lightbox-close').click();
      await expect(lightbox).toBeHidden();
    } finally {
      await deleteJob(jobId, watchPath);
    }
  });
});

// ---- helpers ---------------------------------------------------------------

function makeTinyPng(): Buffer {
  // 1x1 opaque red PNG. Hand-rolled so the spec stays dependency-free.
  // Bytes generated via `pngcheck` round-trip; do not edit by hand.
  return Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNg+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
    'base64'
  );
}
