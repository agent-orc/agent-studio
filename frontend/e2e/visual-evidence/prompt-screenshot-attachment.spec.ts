import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, getJobDetail } from '../helpers/jobs';

/**
 * Prompt editor — pasted/dropped/picked screenshots upload to the job's
 * `attachments/` folder, render inline in the rich-text view, and serialize
 * to prompt.md as a relative `![alt](attachments/<file>.png)` reference so
 * the CLI agent can resolve the same image directly from disk.
 */

interface WatchPath { path: string; name?: string }

// 120×60 RGB gradient — generated once with zlib so the test fixture is
// visibly recognisable in the attached screenshots without pulling in a
// third-party PNG dependency.
const PNG_BYTES = makeGradientPng(120, 60);

function makeGradientPng(width: number, height: number): Buffer {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { deflateSync } = require('zlib') as typeof import('zlib');
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 2; // RGB
  const rowBytes = width * 3;
  const raw = Buffer.alloc(height * (1 + rowBytes));
  for (let y = 0; y < height; y++) {
    const off = y * (1 + rowBytes);
    raw[off] = 0;
    for (let x = 0; x < width; x++) {
      raw[off + 1 + x * 3] = Math.floor((255 * x) / width);
      raw[off + 2 + x * 3] = Math.floor((255 * y) / height);
      raw[off + 3 + x * 3] = 200;
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

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function createPromptJob(): Promise<{ id: string; watchPath: string }> {
  const watchPath = await pickWatchPath();
  const created = await createJob({
    title: `e2e-screenshot-${Date.now()}`,
    watchPath,
    cliType: 'claude',
    agent: 'claude',
    promptMarkdown: '# Screenshot test\n\nDrop a screenshot below:',
    targetState: '2-ready'
  });
  return { id: created.id, watchPath };
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

test.describe('Prompt editor — screenshot attachments', () => {
  test('uploaded image renders inline and serializes to attachments/<file>.png', async ({ page }, testInfo) => {
    const job = await createPromptJob();

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);

      // F48: open the prompt editor from the Files-tab card.
      await page.getByTestId('file-card-prompt-edit').click();

      const editor = page.getByTestId('prompt-editor');
      await expect(editor).toBeVisible({ timeout: 10_000 });

      // Capture the editor in its empty pre-upload state for the chat reply.
      await testInfo.attach('editor-before-upload', {
        body: await editor.screenshot(),
        contentType: 'image/png'
      });

      // Trigger the hidden <input type="file"> — the attach button does this
      // for us, but we set the file directly so we don't depend on a native
      // chooser dialog appearing in headless mode.
      const fileChooserPromise = page.waitForEvent('filechooser');
      await editor.getByTestId('prompt-editor-attach').click();
      const chooser = await fileChooserPromise;
      await chooser.setFiles({
        name: 'screenshot.png',
        mimeType: 'image/png',
        buffer: PNG_BYTES
      });

      // The image should appear inside the rich-text editor with an absolute
      // API URL so the browser can fetch it.
      const renderedImage = editor.locator('.ProseMirror img').first();
      await expect(renderedImage).toBeVisible({ timeout: 10_000 });
      const src = await renderedImage.getAttribute('src');
      expect(src).toBeTruthy();
      expect(src!).toContain(`/api/tasks/${encodeURIComponent(job.id)}/attachments/`);
      expect(src!).toContain(`watchPath=${encodeURIComponent(job.watchPath)}`);

      // The browser must actually be able to load the image (image is decoded).
      const naturalWidth = await renderedImage.evaluate((img: HTMLImageElement) => img.naturalWidth);
      expect(naturalWidth).toBeGreaterThan(0);

      // Ctrl+S persists the new prompt body.
      await page.keyboard.press('Control+s');
      await expect(editor).toHaveAttribute('data-state', 'saved', { timeout: 3_000 });

      // Capture the editor with the rendered screenshot for the chat reply.
      await testInfo.attach('editor-after-upload', {
        body: await editor.screenshot(),
        contentType: 'image/png'
      });

      // Source view must contain a relative `attachments/<file>.png` reference
      // — that's the form prompt.md keeps on disk for the CLI agent.
      await editor.getByTestId('prompt-editor-mode-toggle').click();
      await page.getByTestId('prompt-editor-mode-menu-item-source').click();
      const source = page.getByTestId('prompt-editor-source');
      await expect(source).toBeVisible();
      const sourceText = await source.inputValue();
      expect(sourceText).toMatch(/!\[[^\]]*\]\(attachments\/[a-z0-9]+\.png\)/i);
      expect(sourceText).not.toContain('/api/tasks/');

      // Backend has the same markdown stored in prompt.md.
      const detail = await getJobDetail(job.id, job.watchPath);
      expect(detail.promptMarkdown ?? '').toMatch(/!\[[^\]]*\]\(attachments\/[a-z0-9]+\.png\)/i);

      // The attachment file is reachable via the public URL.
      const match = /\(attachments\/([a-z0-9]+\.png)\)/i.exec(sourceText);
      expect(match).not.toBeNull();
      const fileName = match![1];
      const apiResp = await page.request.get(
        `${BACKEND}/api/tasks/${encodeURIComponent(job.id)}/attachments/${encodeURIComponent(fileName)}?watchPath=${encodeURIComponent(job.watchPath)}`
      );
      expect(apiResp.ok()).toBeTruthy();
      expect(apiResp.headers()['content-type']).toContain('image/');
    } finally {
      await deleteJob(job.id, job.watchPath);
    }
  });
});
