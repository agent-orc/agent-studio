// AGT-3988 Dossier capture config. Copy this folder outside the repository,
// symlink node_modules to <repo>/frontend/node_modules, and start run.sh.
// It reuses the repository's prebuilt Playwright stack without adding a spec to frontend/e2e.
import base from '../../../../frontend/playwright.config';
const FRONTEND = process.env.FRONTEND_ROOT!;
const servers = Array.isArray(base.webServer) ? base.webServer : base.webServer ? [base.webServer] : [];
export default {
  ...base,
  testDir: process.env.CAPTURE_SPECS!,
  reporter: [['list']],
  globalSetup: FRONTEND + '/e2e/prebuilt-global-setup.ts',
  webServer: servers.map(server => ({ ...server, cwd: FRONTEND })),
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
};
