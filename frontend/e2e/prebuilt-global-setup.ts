import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { request } from '@playwright/test';

export default async function globalSetup(): Promise<void> {
  const root = resolve(__dirname, '..', '..');
  const sha = execFileSync('bash', ['scripts/e2e-source-fingerprint.sh'], { cwd: root, encoding: 'utf8' }).trim();
  const published = resolve(root, process.env.PW_E2E_PROFILE === 'public-demo'
    ? 'backend/bin/e2e-publish-public-demo' : 'backend/bin/e2e-publish');
  const browser = resolve(root, 'frontend/dist/frontend/browser');
  for (const [file, label] of [
    [resolve(published, 'E2E-FINGERPRINT'), 'backend'],
    [resolve(browser, '.source-sha'), 'frontend'],
  ]) {
    if (!existsSync(file) || readFileSync(file, 'utf8').trim() !== sha) {
      throw new Error(`Stale ${label} prebuilt artifact. Run npm --prefix frontend run e2e to rebuild.`);
    }
  }
  const response = await fetch(`${process.env.PW_BACKEND_URL}/healthz`);
  if (!response.ok) throw new Error(`Prebuilt backend health returned ${response.status}`);
  if (['networked-monolith', 'operator-cli', 'networked-checkout', 'networked-https-demo'].includes(process.env.PW_E2E_PROFILE ?? '')) {
    // networked-https and networked-https-demo sit behind the self-signed HTTPS test proxy.
    const authRequest = await request.newContext({ ignoreHTTPSErrors: true });
    const auth = await authRequest.get(`${process.env.PW_BASE_URL}/api/auth/status`);
    const status = await auth.json() as { profile?: string; bootstrapRequired?: boolean };
    await authRequest.dispose();
    if (!auth.ok() || status.profile !== 'networked' || status.bootstrapRequired !== true)
      throw new Error('Prebuilt backend is not a fresh networked monolith.');
  } else if (process.env.PW_E2E_PROFILE === 'networked-https') {
    // The throwaway certificate is self-signed; only this check trusts it.
    const tls = await request.newContext({ ignoreHTTPSErrors: true });
    const auth = await tls.get(`${process.env.PW_BASE_URL}/api/auth/status`);
    const status = await auth.json() as { profile?: string; bootstrapRequired?: boolean };
    await tls.dispose();
    if (!auth.ok() || status.profile !== 'networked' || status.bootstrapRequired !== true
        || !process.env.PW_BASE_URL?.startsWith('https://'))
      throw new Error('Prebuilt backend is not a fresh networked monolith behind HTTPS.');
  } else {
    const demoRequest = process.env.PW_E2E_PROFILE === 'public-demo'
      ? await request.newContext({ ignoreHTTPSErrors: true }) : null;
    let paths: { path?: string }[];
    if (demoRequest) {
      const response = await demoRequest.get(`${process.env.PW_BASE_URL}/api/watch-paths`);
      if (!response.ok()) throw new Error(`Prebuilt backend watch paths returned ${response.status()}`);
      paths = await response.json() as { path?: string }[];
    } else {
      const response = await fetch(`${process.env.PW_BACKEND_URL}/api/watch-paths`);
      if (!response.ok) throw new Error(`Prebuilt backend watch paths returned ${response.status}`);
      paths = await response.json() as { path?: string }[];
    }
    const expected = resolve(process.env.PW_E2E_WORKSPACE!, 'projects', ['demo', 'public-demo'].includes(process.env.PW_E2E_PROFILE ?? '') ? 'demo-app' : 'agent-studio-worktree');
    if (!paths.some(item => item.path === expected)) {
      throw new Error('Prebuilt backend is not using the isolated Playwright workspace.');
    }
    if (demoRequest) {
      const auth = await demoRequest.get(`${process.env.PW_BASE_URL}/api/auth/status`);
      const status = await auth.json() as { profile?: string };
      await demoRequest.dispose();
      if (!auth.ok() || status.profile !== 'public-demo-readonly')
        throw new Error('Prebuilt backend is not running the public-demo-readonly profile.');
    }
  }
  if (process.env.PW_E2E_TIMING_FILE && process.env.PW_E2E_TEST_START_MS) {
    writeFileSync(process.env.PW_E2E_TIMING_FILE, JSON.stringify({
      host: process.env.HOSTNAME ?? 'unknown',
      startToReadyMs: Date.now() - Number(process.env.PW_E2E_TEST_START_MS),
      backendPort: process.env.PW_E2E_BACKEND_PORT,
      frontendPort: process.env.PW_E2E_FRONTEND_PORT,
      sourceCommit: sha,
    }, null, 2) + '\n');
  }
}
