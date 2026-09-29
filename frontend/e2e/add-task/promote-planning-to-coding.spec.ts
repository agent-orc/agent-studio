import { test, expect } from '@playwright/test';
import { promises as fs } from 'fs';
import * as path from 'path';

/**
 * Verifies the "Promote to coding task" affordance end-to-end against a real
 * task detail view, with the backend promote endpoint + image bytes mocked at
 * the network boundary (the carrier task is a real completed task on the
 * target backend; we only override the few fields that gate the button plus
 * the new promote payload).
 *
 * Flow: open a finished *planning* task detail -> the promote button shows ->
 * clicking it fetches the prefill draft + pulls the image down -> the existing
 * create-task modal opens prefilled with the title, the extracted prompt body,
 * and the image as a pending attachment.
 */

const PROMOTED_TITLE = 'Promoted: wire up the export pipeline';
const PROMOTED_PROMPT = [
  'Implement the CSV export endpoint described in the planning report.',
  '',
  '- Stream rows from the repository',
  '- Add a `format=csv` query parameter',
  '',
  'See the attached mockup for the column order.',
].join('\n');

const PNG_BYTES = makeGradientPng(160, 90);

test.describe('Promote planning result -> coding task', () => {
  test('finished planning task shows the promote button and pre-fills the create modal (incl. image)', async ({ page }, testInfo) => {
    // Any existing task is a fine "carrier": we fetch its real detail and
    // rewrite only the mode/state, so the rest of the detail view renders
    // from genuine backend data. The promote endpoint + image are mocked
    // (this verifies the wiring without needing a real planning report).
    const carrier = await pickCarrierTask(page);
    const CARRIER_ID = carrier.id;
    const CARRIER_WATCHPATH = carrier.watchPath;

    const detailUrl = new RegExp(`/api/v1/projects/[^/]+/tasks/${escapeRe(CARRIER_ID)}(\\?|$)`);
    const promoteUrl = new RegExp(`/api/tasks/${escapeRe(CARRIER_ID)}/promote-to-coding`);
    const imageUrl = new RegExp(`/api/tasks/${escapeRe(CARRIER_ID)}/results/plan-mockup\\.png`);

    // 1) Rewrite the carrier task's detail so it reads as a *finished planning*
    //    task — that is exactly the gate the promote button checks.
    await page.route(detailUrl, async (route) => {
      if (route.request().method() !== 'GET') return route.continue();
      const res = await route.fetch();
      let body: { info?: { mode?: string; state?: string } };
      try { body = await res.json(); } catch { return route.fulfill({ response: res }); }
      if (body?.info) {
        body.info.mode = 'planning';
        body.info.state = '5-human-review';
      }
      return route.fulfill({ response: res, body: JSON.stringify(body) });
    });

    // 2) Mock the new promote endpoint (this backend build predates it).
    await page.route(promoteUrl, async (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          title: PROMOTED_TITLE,
          promptMarkdown: PROMOTED_PROMPT,
          mode: 'coding',
          targetState: '1-preparation',
          watchPath: CARRIER_WATCHPATH,
          projectName: 'agent-taskboard',
          attachments: [
            {
              fileName: 'plan-mockup.png',
              source: 'results',
              url: `/api/tasks/${CARRIER_ID}/results/plan-mockup.png?watchPath=${encodeURIComponent(CARRIER_WATCHPATH)}`,
            },
          ],
        }),
      }),
    );

    // 3) Serve the promoted image bytes the frontend pulls down before opening
    //    the modal.
    await page.route(imageUrl, async (route) =>
      route.fulfill({ status: 200, contentType: 'image/png', body: PNG_BYTES }),
    );

    // 4) Deep-link straight into the carrier task's detail.
    await page.goto(
      `/?job=${encodeURIComponent(CARRIER_ID)}&watchPath=${encodeURIComponent(CARRIER_WATCHPATH)}`,
    );

    // The promote button only renders for a finished planning task.
    const promoteBtn = page.getByTestId('overview-promote-btn');
    await expect(promoteBtn).toBeVisible({ timeout: 20_000 });
    await expect(promoteBtn).toContainText(/Promote to coding task/i);

    await promoteBtn.scrollIntoViewIfNeeded();
    await saveShot(testInfo, page, '01-promote-button-on-planning-task');

    // 5) Click -> the create modal opens, pre-filled by the promote payload.
    await promoteBtn.click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible({ timeout: 10_000 });

    const titleInput = page.locator('.create-dialog input.field__input').first();
    await expect(titleInput).toHaveValue(PROMOTED_TITLE);

    const promptArea = page.getByTestId('create-prompt');
    await expect(promptArea).toHaveValue(/CSV export endpoint/);
    await expect(promptArea).toHaveValue(/format=csv/);

    // The planning task's image rode along as a pending attachment.
    const attachments = page.getByTestId('create-attachments');
    await expect(attachments).toBeVisible();
    await expect(attachments.locator('img')).toHaveCount(1);

    await saveShot(testInfo, page, '02-create-modal-prefilled-from-planning');

    // The negative side of the gate (research / coding / unfinished planning
    // never show the affordance) is asserted exhaustively in the
    // overview-pane unit spec; keep this E2E focused on the happy path so it
    // does not race context teardown re-navigating a second time.
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });
});

/** Persist a full-page screenshot to the job results dir and the PW report. */
async function saveShot(testInfo: import('@playwright/test').TestInfo, page: import('@playwright/test').Page, name: string): Promise<void> {
  const buf = await page.screenshot({ fullPage: false });
  await testInfo.attach(name, { body: buf, contentType: 'image/png' });
  const dir = process.env.JOB_RESULTS_DIR;
  if (dir) {
    await fs.mkdir(dir, { recursive: true });
    await fs.writeFile(path.join(dir, `promote-${name}.png`), buf);
  }
}

function escapeRe(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** Grab any real task (id + watchPath) from the target backend to act as carrier. */
async function pickCarrierTask(page: import('@playwright/test').Page): Promise<{ id: string; watchPath: string }> {
  const res = await page.request.get('/api/tasks');
  if (!res.ok()) throw new Error(`GET /api/tasks failed: ${res.status()}`);
  const data = (await res.json()) as unknown;
  const arr = (Array.isArray(data)
    ? data
    : ((data as { tasks?: unknown[]; items?: unknown[] }).tasks ??
       (data as { tasks?: unknown[]; items?: unknown[] }).items ??
       [])) as { id?: string; watchPath?: string }[];
  const hit = arr.find((t) => t?.id && t?.watchPath);
  if (!hit?.id || !hit.watchPath) throw new Error('No task with a watchPath available to use as carrier');
  return { id: hit.id, watchPath: hit.watchPath };
}

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
      raw[off + 3 + x * 3] = 140;
    }
  }
  return Buffer.concat([sig, chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);

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
  for (const byte of buf) c = table[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
