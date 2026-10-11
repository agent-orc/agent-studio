import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * Task description image-lightbox: clicking (or double-clicking) an image
 * inside the task description renders it in the shared `app-media-lightbox`.
 *
 * The acceptance scenario from the feature task:
 *   1. A task description contains a markdown image.
 *   2. The user clicks the image.
 *   3. An enlarged preview opens, closable via Escape / close button.
 *
 * The task description editor is the TipTap-based prompt editor. In the
 * normal editable mode a single click selects the image (ProseMirror
 * default) and double-click opens the lightbox; in read-only mode (CLI
 * running) a single click opens it. We exercise the double-click path
 * here since it does not require driving the runner.
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
    headers: { 'X-Client-Id': 'e2e-image-lightbox' }
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
  // F48: prompt is rendered in a Files-tab card; open the editor on demand.
  await page.getByTestId('file-card-prompt-edit').click();
  await expect(page.getByTestId('prompt-editor')).toBeVisible({ timeout: 10_000 });
}

test.describe('Task description image lightbox', () => {
  test('double-clicking a markdown image in the description opens the shared lightbox', async ({ page }, testInfo) => {
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

      // Wait for TipTap to render the image inside the editable surface.
      const img = page.locator('[data-testid="prompt-editor"] .ProseMirror img').first();
      await expect(img).toBeVisible({ timeout: 10_000 });

      // Before-shot of the description with the inline image.
      const promptShot = await page.getByTestId('prompt-editor').screenshot();
      await testInfo.attach('task-description-with-image', { body: promptShot, contentType: 'image/png' });

      // Lightbox should not be in the DOM yet.
      await expect(page.getByTestId('media-lightbox')).toHaveCount(0);

      await img.dblclick();

      const lightbox = page.getByTestId('media-lightbox');
      await expect(lightbox).toBeVisible({ timeout: 5_000 });
      const lbImg = page.getByTestId('media-lightbox-image');
      await expect(lbImg).toBeVisible();
      // The lightbox image src should resolve to the same attachment URL.
      const lbSrc = await lbImg.getAttribute('src');
      expect(lbSrc, 'lightbox src should reference the uploaded attachment').toMatch(/attachments\/demo\.png/);

      const openShot = await page.screenshot({ fullPage: false });
      await testInfo.attach('lightbox-open', { body: openShot, contentType: 'image/png' });

      // Escape closes the lightbox via the modal stack.
      await page.keyboard.press('Escape');
      await expect(lightbox).toBeHidden({ timeout: 2_000 });

      const closedShot = await page.screenshot({ fullPage: false });
      await testInfo.attach('lightbox-closed', { body: closedShot, contentType: 'image/png' });

      // Click-on-image again, then click the explicit close button.
      await img.dblclick();
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
