import { defineConfig, devices } from '@playwright/test';
import base from './playwright.config';

const port = process.env.PW_MOCKED_FRONTEND_PORT;
const assets = process.env.PW_MOCKED_FRONTEND_ASSETS ?? 'dist/frontend/browser';
if (!/^[\w./:-]+$/.test(assets)) throw new Error('PW_MOCKED_FRONTEND_ASSETS must be a safe prebuilt directory path.');
if (!port) throw new Error('PW_MOCKED_FRONTEND_PORT is required. Run npm run e2e:mocked.');

// Opt-in contract fixtures only. These specs intercept every API request and
// can exercise rendering without a live backend or shared task workspace.
export default defineConfig({
  ...base,
  use: { ...base.use, baseURL: `http://127.0.0.1:${port}` },
  fullyParallel: true,
  workers: 4,
  outputDir: 'test-results-mocked',
  projects: [{
    name: 'mocked-api',
    testMatch: [
      '**/system/ui-foundation.spec.ts',
      '**/board/card-state-pill-matches-lane.spec.ts',
      '**/board/identity-chip-card.spec.ts',
      '**/board/project-name-contrast.spec.ts',
      '**/settings/section-picker-navigation.spec.ts',
      '**/board/remote-pickup-mode.spec.ts',
      '**/board/large-board-window.spec.ts',
      '**/board/failed-pickup-lane.spec.ts',
      '**/board/stalled-progress-card.spec.ts',
      '**/board/escalation-bursts.spec.ts',
      '**/board/ready-runner-wait-reasons.spec.ts',
      '**/board/card-host-capability-and-dossier-chip.spec.ts',
      '**/task-detail/pipeline-column-headers.spec.ts',
      '**/task-detail/escalation-summary.spec.ts',
      '**/system/semantic-contrast.spec.ts',
      '**/project/project-deploy-area.spec.ts',
      '**/project/main-scroll-keys.spec.ts',
      '**/project/operations-security.spec.ts',
      '**/project/testing-strategy.spec.ts',
      '**/settings/elevated-sessions.spec.ts',
      '**/workspace/deployment-dashboard.spec.ts',
      '**/workspace/structure-view.spec.ts',
      '**/workspace/structure-live.spec.ts',
      '**/persona-team/team-page.spec.ts',
      '**/project/project-deploy-area.spec.ts',
      '**/studio-shell/activity-bar-single-active.spec.ts',
      '**/visual-evidence/status-indicator-surfaces.spec.ts',
      '**/cli/consumer-model-policy.spec.ts',
      '**/task-detail/task-detail-instant-navigation.spec.ts',
      '**/task-detail/concept-accept-refusal.spec.ts',
      '**/task-detail/acceptance-attempt-gap-core-first.spec.ts',
      '**/task-detail/triage-merge-status.spec.ts',
      '**/task-detail/parked-cause-copy.spec.ts',
      '**/perf/board-bundle-lazy.spec.ts',
      '**/perf/board-window-benchmark.spec.ts',
      '**/batch-gate/batch-flight-queue.spec.ts',
    ],
    use: { ...devices['Desktop Chrome'], channel: undefined, serviceWorkers: 'block' },
  }],
  webServer: {
    command: `node scripts/serve-prebuilt-frontend.mjs ${assets} ${port} http://127.0.0.1:9`,
    url: `http://127.0.0.1:${port}/`,
    timeout: 15_000,
    reuseExistingServer: true,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 5_000 },
  },
});
