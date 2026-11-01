// Capture configuration for the quality-studio-integration Dossier (AGT-3352).
// Run from the repository root after `npm --prefix frontend run build`:
//   bash docs/quality-studio-integration/capture/run.sh
// The dev backend starts only through frontend/e2e/fixtures/dev-backend.ts
// (demo workspace). This file only serves the prebuilt frontend bundle.
const frontendPort = process.env.QSI_FRONTEND_PORT;
const backendPort = process.env.DEV_PORT;
if (!frontendPort || !backendPort) throw new Error('Run capture/run.sh; it allocates QSI_FRONTEND_PORT and DEV_PORT.');

export default {
  testDir: '.',
  testMatch: /capture\.spec\.ts$/,
  timeout: 900_000,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  outputDir: process.env.QSI_OUTPUT_DIR ?? '/tmp/qsi-capture-test-results',
  webServer: {
    command: `node scripts/serve-prebuilt-frontend.mjs dist/frontend/browser ${frontendPort} http://127.0.0.1:${backendPort}`,
    cwd: new URL('../../../frontend/', import.meta.url).pathname,
    url: `http://127.0.0.1:${frontendPort}/`,
    timeout: 15_000,
    reuseExistingServer: false,
  },
  use: {
    baseURL: `http://127.0.0.1:${frontendPort}`,
    viewport: { width: 1600, height: 1000 },
    deviceScaleFactor: 1,
    reducedMotion: 'reduce',
    serviceWorkers: 'block',
    trace: 'off',
    video: 'off',
    screenshot: 'off',
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
  },
};
