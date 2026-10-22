import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { execFileSync } from 'node:child_process';

export default async function globalSetup(): Promise<void> {
  const root = resolve(__dirname, '..', '..');
  const sha = execFileSync('bash', ['scripts/e2e-source-fingerprint.sh'], { cwd: root, encoding: 'utf8' }).trim();
  const published = resolve(root, 'backend/bin/e2e-publish');
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
  if (process.env.PW_E2E_PROFILE === 'networked-monolith') {
    const auth = await fetch(`${process.env.PW_BASE_URL}/api/auth/status`);
    const status = await auth.json() as { profile?: string; bootstrapRequired?: boolean };
    if (!auth.ok || status.profile !== 'networked' || status.bootstrapRequired !== true)
      throw new Error('Prebuilt backend is not a fresh networked monolith.');
  } else {
    const pathsResponse = await fetch(`${process.env.PW_BACKEND_URL}/api/watch-paths`);
    if (!pathsResponse.ok) throw new Error(`Prebuilt backend watch paths returned ${pathsResponse.status}`);
    const paths = await pathsResponse.json() as { path?: string }[];
    const expected = resolve(process.env.PW_E2E_WORKSPACE!, 'projects', process.env.PW_E2E_PROFILE === 'demo' ? 'demo-app' : 'agent-studio-worktree');
    if (!paths.some(item => item.path === expected)) {
      throw new Error('Prebuilt backend is not using the isolated Playwright workspace.');
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
