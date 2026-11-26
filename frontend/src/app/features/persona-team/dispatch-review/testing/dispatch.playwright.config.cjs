const { defineConfig } = require('@playwright/test');
const { resolve } = require('node:path');
const { default: base } = require('../../../../../../playwright.config');

// Keep DS-8 verification in its assigned feature area and reuse the standard stack.
const frontend = resolve(__dirname, '../../../../../..');
module.exports = defineConfig({
  ...base,
  testDir: __dirname,
  testMatch: 'dispatch.playwright.cjs',
  globalSetup: resolve(frontend, 'e2e/prebuilt-global-setup.ts'),
  reporter: [['list']],
  outputDir: resolve(frontend, 'test-results/dispatch'),
  webServer: Array.isArray(base.webServer) ? base.webServer.map(server => ({ ...server, cwd: frontend })) : base.webServer,
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
});
