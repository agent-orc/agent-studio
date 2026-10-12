import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { getJobDetail } from '../helpers/jobs';

/**
 * Add-Task dialog — pasted/dropped/picked screenshots are buffered locally
 * during creation and uploaded to the new job's `attachments/` folder once
 * the job exists. The persisted prompt.md must contain a real
 * `attachments/<file>.png` reference (never the local `pending-attachment-…`
 * placeholder).
 */

interface WatchPath { path: string; name?: string }

const PNG_BYTES = makeGradientPng(120, 60);

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

test.describe('Add Task dialog — image attachments', () => {
  test('attached image is uploaded after create and rewritten in prompt.md', async ({ page }, testInfo) => {
    const wp = await pickWatchPath();
    const titleSlug = `e2e-create-attach-${Date.now()}`;

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    // Pre-fill the title and project so we know where the new job lands.
    const dialog = page.getByRole('dialog').or(page.locator('.create-dialog')).first();
    await expect(dialog).toBeVisible();
    await page.locator('.create-dialog input.field__input').first().fill(titleSlug);
    await page.getByTestId('create-project-select').selectOption({ value: wp.path });

    // Capture the empty dialog before attaching.
    const beforeBuf = await dialog.screenshot();
    await testInfo.attach('add-task-before-attach', { body: beforeBuf, contentType: 'image/png' });
    await require('fs').promises.writeFile('add-task-before-attach.png', beforeBuf);

    // Drive the hidden <input type="file"> via the attach button so we don't
    // depend on a native picker dialog appearing in headless mode.
    const fileChooserPromise = page.waitForEvent('filechooser');
    await page.getByTestId('create-attach-image').click();
    const chooser = await fileChooserPromise;
    await chooser.setFiles({
      name: 'screenshot.png',
      mimeType: 'image/png',
      buffer: PNG_BYTES
    });

    // Thumbnail and placeholder reference should both appear synchronously.
    const attachments = page.getByTestId('create-attachments');
    await expect(attachments).toBeVisible();
    await expect(attachments.locator('img')).toHaveCount(1);

    const promptArea = page.getByTestId('create-prompt');
    await expect(promptArea).toHaveValue(/pending-attachment-/);

    // Scroll the thumbnail row into view so the screenshot captures both the
    // placeholder reference in the prompt and the rendered preview.
    await page.locator('.create-dialog__attachment').first().scrollIntoViewIfNeeded();
    const afterBuf = await dialog.screenshot();
    await testInfo.attach('add-task-after-attach', { body: afterBuf, contentType: 'image/png' });
    await require('fs').promises.writeFile('add-task-after-attach.png', afterBuf);

    // Submit and capture the resulting job id from the API directly.
    await page.getByRole('button', { name: 'Create', exact: true }).click();
    await expect(dialog).toBeHidden({ timeout: 5_000 });

    let detail: Awaited<ReturnType<typeof getJobDetail>> | null = null;
    const start = Date.now();
    while (Date.now() - start < 15_000) {
      try {
        detail = await getJobDetail(titleSlug, wp.path);
        if (detail.promptMarkdown && detail.promptMarkdown.includes('attachments/')) break;
      } catch {
        // job may not exist yet — retry
      }
      await new Promise(r => setTimeout(r, 300));
    }

    try {
      expect(detail, 'job detail should be fetchable').not.toBeNull();
      const prompt = detail!.promptMarkdown ?? '';
      expect(prompt).toMatch(/!\[[^\]]*\]\(attachments\/[a-z0-9]+\.png\)/i);
      expect(prompt).not.toContain('pending-attachment-');

      const match = /\(attachments\/([a-z0-9]+\.png)\)/i.exec(prompt);
      expect(match).not.toBeNull();
      const fileName = match![1];
      const apiResp = await page.request.get(
        `${BACKEND}/api/tasks/${encodeURIComponent(titleSlug)}/attachments/${encodeURIComponent(fileName)}?watchPath=${encodeURIComponent(wp.path)}`
      );
      expect(apiResp.ok()).toBeTruthy();
      expect(apiResp.headers()['content-type']).toContain('image/');
    } finally {
      await deleteJob(titleSlug, wp.path);
    }
  });

  test('removing an attachment also strips its placeholder from the prompt', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const fileChooserPromise = page.waitForEvent('filechooser');
    await page.getByTestId('create-attach-image').click();
    const chooser = await fileChooserPromise;
    await chooser.setFiles({
      name: 'remove-me.png',
      mimeType: 'image/png',
      buffer: PNG_BYTES
    });

    const promptArea = page.getByTestId('create-prompt');
    await expect(promptArea).toHaveValue(/pending-attachment-/);

    await page.locator('.create-dialog__attachment-remove').first().click();
    await expect(page.getByTestId('create-attachments')).toBeHidden();
    await expect(promptArea).toHaveValue('');
  });
});
