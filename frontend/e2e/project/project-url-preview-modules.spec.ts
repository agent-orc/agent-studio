import { createServer } from 'node:http';
import { join } from 'node:path';
import { expect, test } from '../fixtures/dev-backend';

test('proxied module and dynamic import load while Studio API reads stay blocked', async ({ page, devBackend }) => {
  test.setTimeout(180_000);
  const fixture = createServer((request, response) => {
    const path = request.url?.split('?')[0];
    if (path === '/app/' || path === '/app') {
      response.writeHead(200, { 'Content-Type': 'text/html' });
      response.end('<html><head></head><body><script type="module" src="/main.js"></script></body></html>');
    } else if (path === '/app/main.js') {
      response.writeHead(200, { 'Content-Type': 'text/javascript' });
      response.end(`import { value } from '/dependency.js';
      import('./chunk.js').then(module => {
        window.previewLoaded = module.message + value;
        document.body.textContent = 'Preview module loaded: ' + window.previewLoaded;
        fetch('/api/auth/session', { credentials: 'include' })
          .then(() => { window.studioApiAccess = 'readable'; })
          .catch(() => { window.studioApiAccess = 'blocked'; });
      });`);
    } else if (path === '/app/chunk.js') {
      response.writeHead(200, { 'Content-Type': 'text/javascript' });
      response.end("export const message = 'dynamic import loaded';");
    } else if (path === '/app/dependency.js') {
      response.writeHead(200, { 'Content-Type': 'text/javascript' });
      response.end('export const value = 7;');
    } else {
      response.writeHead(404);
      response.end();
    }
  });
  await new Promise<void>(resolve => fixture.listen(0, '127.0.0.1', resolve));
  const address = fixture.address();
  if (!address || typeof address === 'string') throw new Error('Fixture did not bind a TCP port.');
  let projectId: string | undefined;
  let urlId: string | undefined;
  let clientId: string | undefined;
  try {
    const projectsResponse = await page.request.get(`${devBackend.baseUrl}/api/projects`);
    expect(projectsResponse.ok()).toBe(true);
    const projects = await projectsResponse.json() as { id: string }[];
    projectId = projects[0]?.id;
    expect(projectId).toBeTruthy();
    const label = `Module fixture ${address.port}`;
    const identity = await page.request.post(`${devBackend.baseUrl}/api/clients/register`, {
      data: { displayName: label },
    });
    expect(identity.ok(), await identity.text()).toBe(true);
    clientId = (await identity.json() as { id: string }).id;
    const register = await page.request.post(`${devBackend.baseUrl}/api/projects/${projectId}/urls`, {
      headers: { 'X-Client-Id': clientId },
      data: { label, url: `http://127.0.0.1:${address.port}/app` },
    });
    expect(register.ok(), await register.text()).toBe(true);
    const updated = await register.json() as { urls: { id: string; label: string }[] };
    urlId = updated.urls.find(row => row.label === label)?.id;
    expect(urlId).toBeTruthy();

    await page.goto(`${devBackend.baseUrl}/api/projects/${projectId}/urls/${urlId}/preview/`);
    await expect.poll(() => page.evaluate(() => (window as Window & { previewLoaded?: string }).previewLoaded))
      .toBe('dynamic import loaded7');
    await expect(page.locator('body')).toHaveText('Preview module loaded: dynamic import loaded7');
    await expect.poll(() => page.evaluate(() => (window as Window & { studioApiAccess?: string }).studioApiAccess))
      .toBe('blocked');
    expect(await page.evaluate(() => globalThis.origin)).toBe('null');
    expect(await page.evaluate(() => {
      try { return document.cookie; }
      catch { return 'blocked'; }
    })).toBe('blocked');
    if (process.env.JOB_RESULTS_DIR) {
      await page.screenshot({ path: join(process.env.JOB_RESULTS_DIR, 'agt-3196-proxy-module.png') });
    }
  } finally {
    if (projectId && urlId && clientId) {
      await page.request.delete(`${devBackend.baseUrl}/api/projects/${projectId}/urls/${urlId}`, {
        headers: { 'X-Client-Id': clientId },
      });
    }
    if (clientId) {
      await page.request.delete(`${devBackend.baseUrl}/api/clients/${clientId}`, {
        headers: { 'X-Client-Id': clientId },
      });
    }
    await new Promise<void>((resolve, reject) => fixture.close(error => error ? reject(error) : resolve()));
  }
});
