import { defineConfig, devices } from '@playwright/test';
import base from './playwright.config';

const port = process.env.PW_MOCKED_FRONTEND_PORT;
if (!port) throw new Error('PW_MOCKED_FRONTEND_PORT is required. Run npm run e2e:mocked.');

// Opt-in contract fixtures only. These specs intercept every API request and
// can exercise rendering without a live backend or shared task workspace.
export default defineConfig({
  ...base,
  fullyParallel: true,
  workers: 4,
  outputDir: 'test-results-mocked',
  projects: [{
    name: 'mocked-api',
    testMatch: [
      '**/board/card-state-pill-matches-lane.spec.ts',
      '**/board/large-board-window.spec.ts',
      '**/board/failed-pickup-lane.spec.ts',
      '**/board/stalled-progress-card.spec.ts',
      '**/board/escalation-bursts.spec.ts',
      '**/board/ready-runner-wait-reasons.spec.ts',
      '**/board/card-host-capability-and-dossier-chip.spec.ts',
      '**/task-detail/pipeline-column-headers.spec.ts',
      '**/system/semantic-contrast.spec.ts',
      '**/project/project-deploy-area.spec.ts',
      '**/project/operations-security.spec.ts',
      '**/workspace/deployment-dashboard.spec.ts',
      '**/workspace/structure-view.spec.ts',
      '**/project/project-deploy-area.spec.ts',
      '**/studio-shell/activity-bar-single-active.spec.ts',
      '**/cli/consumer-model-policy.spec.ts',
      '**/task-detail/task-detail-instant-navigation.spec.ts',
      '**/task-detail/acceptance-attempt-gap-core-first.spec.ts',
      '**/task-detail/triage-merge-status.spec.ts',
      '**/perf/board-bundle-lazy.spec.ts',
      '**/perf/board-window-benchmark.spec.ts',
    ],
    use: { ...devices['Desktop Chrome'], channel: undefined, serviceWorkers: 'block' },
  }],
  webServer: {
    command: `node scripts/serve-prebuilt-frontend.mjs dist/frontend/browser ${port} http://127.0.0.1:9`,
    url: `http://127.0.0.1:${port}/`,
    timeout: 15_000,
    reuseExistingServer: true,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 5_000 },
  },
});
