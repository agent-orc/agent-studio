import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright configuration for Agent Software Studio frontend E2E tests.
 *
 * Conventions (read this before writing tests):
 * - The default `npm run e2e` prepares prebuilt assets once and starts an
 *   isolated stack on dynamic ports. Direct `playwright test` retains the
 *   existing externally managed target behavior for interactive use.
 * - Externally managed targets: DEV at frontend 4010, STABLE at 4011.
 * - Choose target via env vars (in order of precedence):
 *     - PW_BASE_URL=http://...    explicit override
 *     - PW_TARGET=stable          http://localhost:4011
 *     - PW_TARGET=dev   (default) http://localhost:4010
 *   Task verification should use the isolated prebuilt stack instead.
 * - Single-browser target (chromium headless-shell) to keep the install small.
 * - Use stable selectors: prefer `data-testid="..."`, then role/label, then text.
 *   Never select by CSS class — they change with styling work.
 * - Tests must clean up any state they create (jobs, sessions). See
 *   `e2e/helpers/jobs.ts` for the API-level cleanup helpers.
 * - JobArtifactReporter activates when JOB_RESULTS_DIR env var is set (agent task orchestrator).
 *   It harvests test artifacts into <JOB_RESULTS_DIR>/playwright/ with summary index.json.
 *   Not used during local development.
 */
const pwTarget = (process.env.PW_TARGET ?? 'dev').toLowerCase();
const prebuilt = process.env.PW_PREBUILT === '1';
const profile = process.env.PW_E2E_PROFILE ?? 'standard';
const backendPort = process.env.PW_E2E_BACKEND_PORT;
const frontendPort = process.env.PW_E2E_FRONTEND_PORT;
if (prebuilt && (!backendPort || !frontendPort || !process.env.PW_E2E_WORKSPACE)) {
  throw new Error('PW_PREBUILT=1 requires ports and an isolated PW_E2E_WORKSPACE. Run npm run e2e.');
}
const resolvedBaseUrl =
  process.env.PW_BASE_URL?.trim()
  || (pwTarget === 'stable' ? 'http://localhost:4011' : 'http://localhost:4010');

const reporters: any[] = [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]];
// Activate job artifact reporter only when JOB_RESULTS_DIR is set (agent task orchestrator mode).
if (process.env.JOB_RESULTS_DIR) {
  reporters.push(['./e2e/helpers/job-artifact-reporter.ts']);
}

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  failOnFlakyTests: !!process.env.CI,
  workers: 1,
  reporter: reporters,
  ...(prebuilt ? {
    globalSetup: './e2e/prebuilt-global-setup.ts',
    webServer: [
      {
        command: 'bash ../scripts/serve-prebuilt-backend.sh',
        url: `http://127.0.0.1:${backendPort}/healthz`,
        timeout: 100_000,
        reuseExistingServer: true,
        gracefulShutdown: { signal: 'SIGTERM' as const, timeout: 30_000 },
      },
      {
        command: `node scripts/serve-prebuilt-frontend.mjs dist/frontend/browser ${frontendPort} http://127.0.0.1:${backendPort}`,
        url: `http://127.0.0.1:${frontendPort}/`,
        timeout: 15_000,
        reuseExistingServer: true,
        gracefulShutdown: { signal: 'SIGTERM' as const, timeout: 5_000 },
      },
    ],
  } : {}),
  use: {
    baseURL: resolvedBaseUrl,
    trace: process.env.CI ? 'on-first-retry' : 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    actionTimeout: 10_000,
    navigationTimeout: 15_000,
    serviceWorkers: 'block',
  },
  projects: [
    {
      name: 'chromium',
      ...(prebuilt ? {
        testMatch: profile === 'recovery' ? ['**/system/crash-recovery-prompt.spec.ts', '**/settings/first-run-checklist.spec.ts']
          : profile === 'ref-drift' ? '**/system/crash-recovery-prompt.spec.ts'
          : profile === 'demo' ? '**/usage-cockpit/cockpit-integration-matrix.spec.ts' : '**/*.spec.ts',
        testIgnore: profile === 'standard'
          ? /visual-evidence\/|system\/crash-recovery-prompt\.spec\.ts|usage-cockpit\/cockpit-integration-matrix\.spec\.ts/
          : /visual-evidence\//,
      } : { testIgnore: /visual-evidence\// }),
      use: { ...devices['Desktop Chrome'], channel: undefined },
    },
    {
      name: 'visual-evidence',
      testMatch: profile === 'standard' ? /visual-evidence\/.*\.spec\.ts/ : /$^/,
      use: { ...devices['Desktop Chrome'], channel: undefined, video: 'retain-on-failure' },
    },
  ],
  outputDir: 'test-results'
});
