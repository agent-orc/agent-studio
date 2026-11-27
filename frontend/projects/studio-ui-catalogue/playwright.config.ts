import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  workers: 4,
  retries: 0,
  timeout: 90_000,
  snapshotPathTemplate: '{testDir}/snapshots/{arg}{ext}',
  expect: { toHaveScreenshot: { animations: 'disabled', maxDiffPixels: 0 } },
  use: { baseURL: 'http://127.0.0.1:24771', ...devices['Desktop Chrome'], serviceWorkers: 'block', trace: 'off', video: 'off' },
  webServer: {
    command: 'node scripts/serve-prebuilt-frontend.mjs dist/studio-ui-catalogue/browser 24771 http://127.0.0.1:9',
    cwd: '../..', url: 'http://127.0.0.1:24771', reuseExistingServer: false,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 5000 },
  },
  reporter: [['list']],
  outputDir: '../../test-results-ui',
});
