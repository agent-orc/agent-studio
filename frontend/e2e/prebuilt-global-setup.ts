import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { execFileSync } from 'node:child_process';

export default async function globalSetup(): Promise<void> {
  const root = resolve(__dirname, '..', '..');
  const sha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
  const published = resolve(root, 'backend/bin/e2e-publish');
  const browser = resolve(root, 'frontend/dist/frontend/browser');
  for (const [file, label] of [
    [resolve(published, 'RELEASE-SHA'), 'backend'],
    [resolve(browser, '.source-sha'), 'frontend'],
  ]) {
    if (!existsSync(file) || readFileSync(file, 'utf8').trim() !== sha) {
      throw new Error(`Stale ${label} prebuilt artifact. Run npm --prefix frontend run e2e to rebuild.`);
    }
  }
  const response = await fetch(`${process.env.PW_BACKEND_URL}/healthz`);
  if (!response.ok) throw new Error(`Prebuilt backend health returned ${response.status}`);
  const pathsResponse = await fetch(`${process.env.PW_BACKEND_URL}/api/watch-paths`);
  if (!pathsResponse.ok) throw new Error(`Prebuilt backend watch paths returned ${pathsResponse.status}`);
  const paths = await pathsResponse.json() as { path?: string }[];
  const expected = resolve(process.env.PW_E2E_WORKSPACE!, 'projects/agent-studio');
  if (!paths.some(item => item.path === expected)) {
    throw new Error('Prebuilt backend is not using the isolated Playwright workspace.');
  }
}
