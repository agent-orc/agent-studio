/**
 * Playwright fixture: dev backend lifecycle for specs running from stable.
 *
 * Why this exists: dev is the regression-test target. By convention dev's
 * backend is offline; only Playwright specs that explicitly need it may bring
 * it up. This fixture is the single way to do that. It calls
 * `scripts/supervisor/dev-lifecycle.sh` to start dev's backend on :5030 before
 * the spec runs and tears it down after, while staying idempotent: if the dev
 * backend was already healthy and exposes a usable watch path when the fixture
 * loaded, the fixture leaves it running on teardown.
 *
 * Set `KEEP_DEV_ON_FAIL=1` to keep dev up after a failing test for inspection.
 *
 * Resolution rules (no hard-coded paths):
 *   - DEV_CHECKOUT env var wins.
 *   - A git worktree runs the backend from that worktree, so task verification
 *     never falls through to a sibling checkout.
 *   - A temporary task repository and watched project point back to the
 *     selected checkout for repository provenance, so an isolated checkout
 *     without appsettings.Local.json can still drive real-source UI coverage.
 *   - Else: ask the dev backend's `/api/watch-paths` endpoint after start
 *     (Agent Software Studio entry) for the workspace path.
 *   - Else: fall back to the script's own default (sibling folder).
 *
 * The fixture exposes:
 *   - port:      the dev backend port (number, default 5030).
 *   - baseUrl:   `http://127.0.0.1:<port>` for direct REST calls.
 *   - workspace: the dev checkout path (string), as reported by the backend.
 */
import { test as base, expect } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import * as path from 'node:path';

export interface DevBackend {
  port: number;
  baseUrl: string;
  workspace: string;
}

const DEV_PORT = Number(process.env.DEV_PORT ?? 5030);
const DEV_BASE_URL = `http://127.0.0.1:${DEV_PORT}`;
let isolatedWorkspace: string | undefined;

function ensureIsolatedWorkspace(): { taskRepository: string; watchPath: string } {
  isolatedWorkspace ??= mkdtempSync(path.join(tmpdir(), 'agent-studio-dev-backend-'));
  const watchPath = path.join(isolatedWorkspace, 'projects', 'agent-studio-worktree');
  mkdirSync(watchPath, { recursive: true });
  return { taskRepository: isolatedWorkspace, watchPath };
}

function resolveRepoRoot(): string {
  return path.resolve(__dirname, '..', '..', '..');
}

function resolveDevCheckout(): string | undefined {
  if (process.env.DEV_CHECKOUT) return process.env.DEV_CHECKOUT;

  const repoRoot = resolveRepoRoot();
  const gitMarker = path.join(repoRoot, '.git');
  if (existsSync(gitMarker) && statSync(gitMarker).isFile()) return repoRoot;

  return undefined;
}

function resolveScriptPath(): string {
  // The fixture file lives at <repo>/frontend/e2e/fixtures/dev-backend.ts.
  // The script lives at <repo>/scripts/supervisor/dev-lifecycle.sh.
  // __dirname is not available in ESM; derive from import.meta-style cwd.
  return path.join(resolveRepoRoot(), 'scripts', 'supervisor', 'dev-lifecycle.sh');
}

function runScript(cmd: 'start' | 'stop' | 'status'): { code: number; stdout: string; stderr: string } {
  const scriptPath = resolveScriptPath();
  const devCheckout = resolveDevCheckout();
  const isolated = cmd === 'start' && devCheckout ? ensureIsolatedWorkspace() : undefined;
  if (!existsSync(scriptPath)) {
    throw new Error(`dev-lifecycle.sh not found at ${scriptPath}`);
  }
  // Use bash explicitly so this works on Windows Git Bash and Linux/macOS.
  const result = spawnSync('bash', [scriptPath, cmd], {
    env: {
      ...process.env,
      DEV_PORT: String(DEV_PORT),
      ...(devCheckout ? { DEV_CHECKOUT: devCheckout } : {}),
      ...(isolated && !process.env.WatchPaths__0__Path ? {
        TaskRepository: isolated.taskRepository,
        Runner__Role: 'test-subject',
        WatchPaths__0__Name: 'Agent Studio Worktree',
        WatchPaths__0__Path: isolated.watchPath,
        WatchPaths__0__RootPath: devCheckout,
        WatchPaths__0__RepositoryPath: devCheckout,
      } : {}),
    },
    encoding: 'utf8',
    timeout: 200_000, // api.sh allows 180 seconds for a cold backend build.
  });
  return {
    code: result.status ?? 1,
    stdout: result.stdout ?? '',
    stderr: result.stderr ?? '',
  };
}

async function isHealthy(): Promise<boolean> {
  try {
    const res = await fetch(`${DEV_BASE_URL}/healthz`, { signal: AbortSignal.timeout(2000) });
    return res.ok;
  } catch {
    return false;
  }
}

async function hasWatchPath(): Promise<boolean> {
  try {
    const res = await fetch(`${DEV_BASE_URL}/api/watch-paths`, { signal: AbortSignal.timeout(5000) });
    if (!res.ok) return false;
    const paths: unknown[] = await res.json();
    return paths.length > 0;
  } catch {
    return false;
  }
}

async function discoverWorkspace(): Promise<string> {
  const checkout = resolveDevCheckout();
  if (checkout) return checkout;
  try {
    const res = await fetch(`${DEV_BASE_URL}/api/watch-paths`, { signal: AbortSignal.timeout(5000) });
    if (res.ok) {
      const paths: { name?: string; rootPath?: string }[] = await res.json();
      const ours = paths.find(p => (p.rootPath ?? '').toLowerCase().includes('agent-taskboard-dev'));
      if (ours?.rootPath) return ours.rootPath;
    }
  } catch {
    // fall through
  }
  // Last resort: same default the script uses.
  return path.resolve(resolveRepoRoot(), '..', 'agent-taskboard-dev');
}

export const test = base.extend<{ devBackend: DevBackend }>({
  // Playwright 1.59 requires object destructuring even when the fixture has no dependencies.
  // eslint-disable-next-line no-empty-pattern
  devBackend: async ({}, use, testInfo) => {
    const startedHealthy = await isHealthy();
    let weStartedIt = false;

    // A fixture-started worktree backend needs the isolated WatchPaths values
    // passed by runScript('start'). A stale KEEP_DEV_ON_FAIL process can still
    // be healthy while carrying an empty configuration, so recycle only that
    // incompatible case instead of silently yielding an unusable subject.
    if (startedHealthy && resolveDevCheckout() && !await hasWatchPath()) {
      const stopped = runScript('stop');
      if (stopped.code !== 0) {
        throw new Error(
          `Could not recycle the incompatible dev backend (exit ${stopped.code}).\nstdout:\n${stopped.stdout}\nstderr:\n${stopped.stderr}`
        );
      }
    }

    if (!startedHealthy || !await isHealthy()) {
      const r = runScript('start');
      if (r.code !== 0) {
        runScript('stop');
        if (isolatedWorkspace) {
          rmSync(isolatedWorkspace, { recursive: true, force: true });
          isolatedWorkspace = undefined;
        }
        throw new Error(
          `dev-lifecycle.sh start failed (exit ${r.code}).\nstdout:\n${r.stdout}\nstderr:\n${r.stderr}`
        );
      }
      weStartedIt = true;
      // Belt-and-braces: confirm before yielding.
      await expect.poll(() => isHealthy(), { timeout: 30_000, intervals: [500, 1000, 2000] }).toBe(true);
    }

    const workspace = await discoverWorkspace();

    await use({ port: DEV_PORT, baseUrl: DEV_BASE_URL, workspace });

    // Teardown: only stop what we started, and respect KEEP_DEV_ON_FAIL.
    if (!weStartedIt) return;
    const failed = testInfo.status !== testInfo.expectedStatus;
    if (failed && process.env.KEEP_DEV_ON_FAIL === '1') {
      console.log('[dev-backend fixture] test failed; KEEP_DEV_ON_FAIL=1 set, leaving dev backend running for inspection.');
      return;
    }
    const r = runScript('stop');
    if (r.code !== 0) {
      console.warn(`[dev-backend fixture] dev-lifecycle.sh stop returned ${r.code}\n${r.stderr}`);
    }
    if (isolatedWorkspace) {
      rmSync(isolatedWorkspace, { recursive: true, force: true });
      isolatedWorkspace = undefined;
    }
  },
});

export { expect };
