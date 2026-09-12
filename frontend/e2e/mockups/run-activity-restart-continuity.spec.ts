import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import http from 'node:http';
import type { AddressInfo } from 'node:net';
import path from 'node:path';

const DIST_DIR = path.resolve(__dirname, '..', '..', 'dist', 'run-activity-pill-mockup', 'browser');
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  || path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'restart-continuity');

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
};

let server: http.Server;
let baseUrl: string;

test.beforeAll(async () => {
  if (!fs.existsSync(path.join(DIST_DIR, 'index.html'))) {
    throw new Error(`Missing ${DIST_DIR}. Build run-activity-pill-mockup first.`);
  }
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  server = http.createServer((req, res) => {
    const requestPath = decodeURIComponent((req.url ?? '/').split('?')[0]);
    const filePath = path.join(DIST_DIR, requestPath === '/' ? 'index.html' : requestPath);
    if (!fs.existsSync(filePath) || fs.statSync(filePath).isDirectory()) {
      res.statusCode = 404;
      res.end('not found');
      return;
    }
    res.setHeader('Content-Type', MIME[path.extname(filePath)] ?? 'application/octet-stream');
    fs.createReadStream(filePath).pipe(res);
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address() as AddressInfo;
  baseUrl = `http://127.0.0.1:${port}/`;
});

test.afterAll(async () => {
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

test.describe('@mockup restart continuity activity', () => {
  test('shows the adopted run and original worker PID in both themes', async ({ page }) => {
    await page.setViewportSize({ width: 1180, height: 850 });
    await page.goto(baseUrl);

    const scenario = page.locator('[data-scenario="continuing-after-restart"]');
    await expect(scenario.getByTestId('task-card-run-activity'))
      .toHaveText(/continuing after restart/i);
    await expect(scenario.getByTestId('tooltip-continuing-after-restart'))
      .toContainText('Studio reattached to the durable worker');
    await expect(scenario.getByTestId('tooltip-continuing-after-restart'))
      .toContainText('48212');

    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate((selected) => {
        if (selected === 'light') document.documentElement.setAttribute('data-studio-theme', 'light');
        else document.documentElement.removeAttribute('data-studio-theme');
      }, theme);
      await scenario.screenshot({
        path: path.join(RESULTS_DIR, `continuing-after-restart-card--${theme}.png`),
      });
    }
  });
});
