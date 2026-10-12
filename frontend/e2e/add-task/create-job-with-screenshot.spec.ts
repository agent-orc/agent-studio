import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { getJobDetail } from '../helpers/jobs';

/**
 * Add-Task dialog - drop + paste paths for screenshots.
 *
 * Companion to `add-task-attachment.spec.ts` (which covers the file-picker
 * path). The reported bug was that users could not attach screenshots:
 * the dialog had no visible drop zone and paste only fired inside the
 * textarea, so Ctrl+V on an empty dialog was a silent no-op. After the
 * fix, paste is bound on the dialog wrapper and the prompt textarea
 * auto-focuses on open so Ctrl+V works without an extra click.
 */

interface WatchPath { path: string; name?: string }

const PNG_BYTES = makeGradientPng(120, 60);
const PNG_BASE64 = PNG_BYTES.toString('base64');

function makeGradientPng(width: number, height: number): Buffer {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { deflateSync } = require('zlib') as typeof import('zlib');
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 2;
  const rowBytes = width * 3;
  const raw = Buffer.alloc(height * (1 + rowBytes));
  for (let y = 0; y < height; y++) {
    const off = y * (1 + rowBytes);
    raw[off] = 0;
    for (let x = 0; x < width; x++) {
      raw[off + 1 + x * 3] = Math.floor((255 * x) / width);
      raw[off + 2 + x * 3] = Math.floor((255 * y) / height);
      raw[off + 3 + x * 3] = 180;
    }
  }
  return Buffer.concat([
    sig,
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0))
  ]);

  function chunk(type: string, data: Buffer): Buffer {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length, 0);
    const t = Buffer.from(type, 'ascii');
    const c = Buffer.alloc(4);
    c.writeUInt32BE(crc32(Buffer.concat([t, data])), 0);
    return Buffer.concat([len, t, data, c]);
  }
}

function crc32(buf: Buffer): number {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = table[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE'
    });
  } catch {
    // best-effort cleanup
  }
}

async function openDialogWithTitleAndProject(
  page: Page,
  titleSlug: string,
  watchPath: string,
): Promise<void> {
  await page.goto('/');
  await page.getByRole('button', { name: /add task/i }).first().click();
  const dialog = page.locator('.create-dialog');
  await expect(dialog).toBeVisible();
  await page.locator('.create-dialog input.field__input').first().fill(titleSlug);
  await page.getByTestId('create-project-select').selectOption({ value: watchPath });
}

/**
 * Dispatches a synthetic paste event carrying a PNG `File` on the dialog
 * wrapper. Mirrors what the browser fires when the user hits Ctrl+V while
 * focused inside the dialog. We target the wrapper specifically to prove
 * the wrapper-level (paste) handler catches the event regardless of which
 * field has focus.
 */
async function pastePngOnDialog(page: Page, pngBase64: string): Promise<void> {
  await page.evaluate((b64: string) => {
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    const file = new File([bytes], 'pasted.png', { type: 'image/png' });
    const dt = new DataTransfer();
    dt.items.add(file);
    const target = document.querySelector('.create-dialog') as HTMLElement | null;
    if (!target) throw new Error('Create dialog not in DOM');
    const evt = new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true });
    target.dispatchEvent(evt);
  }, pngBase64);
}

/**
 * Dispatches synthetic dragover + drop events with a PNG file on the
 * dialog wrapper. Mirrors what the browser fires when the user drags
 * a screenshot from their file manager onto the dialog.
 */
async function dropPngOnDialog(page: Page, pngBase64: string): Promise<void> {
  await page.evaluate((b64: string) => {
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    const file = new File([bytes], 'dropped.png', { type: 'image/png' });
    const dt = new DataTransfer();
    dt.items.add(file);
    const target = document.querySelector('.create-dialog') as HTMLElement | null;
    if (!target) throw new Error('Create dialog not in DOM');
    const over = new DragEvent('dragover', { dataTransfer: dt, bubbles: true, cancelable: true });
    target.dispatchEvent(over);
    const drop = new DragEvent('drop', { dataTransfer: dt, bubbles: true, cancelable: true });
    target.dispatchEvent(drop);
  }, pngBase64);
}

async function waitForPersistedAttachment(
  jobId: string,
  watchPath: string,
  timeoutMs = 15_000,
): Promise<string> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try {
      const detail = await getJobDetail(jobId, watchPath);
      const prompt = detail.promptMarkdown ?? '';
      const match = /!\[[^\]]*\]\(attachments\/([a-z0-9]+\.png)\)/i.exec(prompt);
      if (match) return match[1];
    } catch {
      // job may not exist yet - retry
    }
    await new Promise(r => setTimeout(r, 300));
  }
  throw new Error(`prompt.md never resolved attachments/<file>.png within ${timeoutMs}ms`);
}

test.describe('Add Task dialog - drop + paste screenshot uploads', () => {
  test('the dialog shows a visible drop+paste hint banner on open', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();
    const dropzone = page.getByTestId('create-dropzone');
    await expect(dropzone).toBeVisible();
    // Hint text mentions paste with Ctrl+V and a click-to-browse fallback,
    // so users discover all three input paths without a tooltip dance.
    await expect(dropzone).toContainText(/paste/i);
    await expect(dropzone).toContainText(/Ctrl/);
    await expect(dropzone).toContainText(/V/);
    await expect(dropzone).toContainText(/click/i);
  });

  test('paste (Ctrl+V) attaches the clipboard PNG and persists after Create', async ({ page }) => {
    const wp = await pickWatchPath();
    const titleSlug = `e2e-create-paste-${Date.now()}`;
    await openDialogWithTitleAndProject(page, titleSlug, wp.path);

    await pastePngOnDialog(page, PNG_BASE64);

    const attachments = page.getByTestId('create-attachments');
    await expect(attachments).toBeVisible();
    await expect(attachments.locator('img')).toHaveCount(1);
    await expect(page.getByTestId('create-prompt')).toHaveValue(/pending-attachment-/);

    await page.getByRole('button', { name: 'Create', exact: true }).click();
    await expect(page.locator('.create-dialog')).toBeHidden({ timeout: 5_000 });

    try {
      const fileName = await waitForPersistedAttachment(titleSlug, wp.path);
      const resp = await page.request.get(
        `${BACKEND}/api/tasks/${encodeURIComponent(titleSlug)}/attachments/${encodeURIComponent(fileName)}?watchPath=${encodeURIComponent(wp.path)}`
      );
      expect(resp.ok()).toBeTruthy();
      expect(resp.headers()['content-type']).toContain('image/');
    } finally {
      await deleteJob(titleSlug, wp.path);
    }
  });

  test('drag-and-drop attaches the dropped PNG and persists after Create', async ({ page }) => {
    const wp = await pickWatchPath();
    const titleSlug = `e2e-create-drop-${Date.now()}`;
    await openDialogWithTitleAndProject(page, titleSlug, wp.path);

    await dropPngOnDialog(page, PNG_BASE64);

    const attachments = page.getByTestId('create-attachments');
    await expect(attachments).toBeVisible();
    await expect(attachments.locator('img')).toHaveCount(1);
    await expect(page.getByTestId('create-prompt')).toHaveValue(/pending-attachment-/);

    await page.getByRole('button', { name: 'Create', exact: true }).click();
    await expect(page.locator('.create-dialog')).toBeHidden({ timeout: 5_000 });

    try {
      const fileName = await waitForPersistedAttachment(titleSlug, wp.path);
      const resp = await page.request.get(
        `${BACKEND}/api/tasks/${encodeURIComponent(titleSlug)}/attachments/${encodeURIComponent(fileName)}?watchPath=${encodeURIComponent(wp.path)}`
      );
      expect(resp.ok()).toBeTruthy();
      expect(resp.headers()['content-type']).toContain('image/');
    } finally {
      await deleteJob(titleSlug, wp.path);
    }
  });
});
