// The browser half of the scenario: a Chromium page that signs in through the
// Studio sign-in form, opens Studio views and the stage and production
// addresses, and saves screenshots into the report directory. Playwright comes
// from the frontend checkout, so the scenario adds no dependency.
import { mkdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const FRONTEND = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../frontend');
const VIEWPORT = { width: 1440, height: 900 };
const NAVIGATION_TIMEOUT_MS = 60_000;
// Studio keeps event streams open, so the network may never go idle; settle briefly instead.
const SETTLE_MS = 10_000;

export async function loadChromium() {
  const require = createRequire(path.join(FRONTEND, 'package.json'));
  try {
    return require('playwright').chromium;
  } catch {
    throw new Error('Playwright is missing. Run: bash scripts/npm-frontend.sh ci');
  }
}

/**
 * Opens a browser for one run. `screenshotDir` is inside the report
 * directory; returned paths are relative to the report so the HTML links work.
 */
export async function openBrowser({ studioUrl, reportDir, chromium }) {
  const engine = chromium ?? await loadChromium();
  const browser = await engine.launch({ headless: true });
  const context = await browser.newContext({ viewport: VIEWPORT, ignoreHTTPSErrors: false });
  const page = await context.newPage();
  page.setDefaultNavigationTimeout(NAVIGATION_TIMEOUT_MS);
  const shotDir = path.join(reportDir, 'screenshots');
  await mkdir(shotDir, { recursive: true });
  let counter = 0;
  const studio = new URL(studioUrl);

  return {
    /** Signs in through the Studio sign-in form, as the simulated user would. */
    async signIn({ login, password, totpCode }) {
      // auth-gate.html: the sign-in form of a networked installation.
      await page.goto(new URL('/', studio).href, { waitUntil: 'domcontentloaded' });
      const gate = page.getByTestId('auth-gate');
      const user = gate.locator('input[name="username"]');
      await user.waitFor({ timeout: NAVIGATION_TIMEOUT_MS });
      await user.fill(login);
      await gate.locator('input[name="password"][autocomplete="current-password"]').fill(password);
      if (totpCode) await gate.locator('input[name="secondFactorCode"]').first().fill(totpCode);
      // Wait before the click: a fast answer would otherwise arrive unobserved.
      const answered = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/auth/login'),
        { timeout: NAVIGATION_TIMEOUT_MS });
      await gate.getByRole('button', { name: 'Sign in', exact: true }).click();
      const answer = await answered;
      if (!answer.ok()) throw new Error(`Studio refused the browser sign-in (${answer.status()}).`);
      await page.waitForLoadState('networkidle', { timeout: SETTLE_MS }).catch(() => undefined);
    },

    /** Opens a Studio hash route such as /#/projects/PROJ-012/deploy. */
    async openStudio(route) {
      await page.goto(new URL(route, studio).href, { waitUntil: 'domcontentloaded' });
      await page.waitForLoadState('networkidle', { timeout: SETTLE_MS }).catch(() => undefined);
    },

    /** Opens any address (stage, production) and returns status and visible text. */
    async openAddress(address) {
      const response = await page.goto(address, { waitUntil: 'load' });
      return { status: response?.status() ?? null, text: (await page.locator('body').innerText()).slice(0, 2000) };
    },

    /** The accessible names of the buttons on the open page. */
    async buttonNames() {
      return page.getByRole('button').evaluateAll(buttons =>
        buttons.map(button => (button.getAttribute('aria-label') || button.textContent || '').trim()).filter(Boolean));
    },

    /** Clicks the first button with this exact name; returns the visible text afterwards. */
    async clickButton(name) {
      await page.getByRole('button', { name, exact: true }).first().click();
      return (await page.locator('body').innerText()).slice(0, 2000);
    },

    /** Waits briefly for a data-testid; returns whether it shows and its data-status. */
    async testIdState(testId, timeoutMs = 15_000) {
      const element = page.getByTestId(testId).first();
      const visible = await element.waitFor({ state: 'visible', timeout: timeoutMs }).then(() => true, () => false);
      return {
        visible,
        status: visible ? await element.getAttribute('data-status') : null,
        text: visible ? (await element.innerText()).slice(0, 500) : null,
      };
    },

    async screenshot(name) {
      counter += 1;
      const file = `${String(counter).padStart(2, '0')}-${name}.png`;
      await page.screenshot({ path: path.join(shotDir, file), fullPage: false });
      return `screenshots/${file}`;
    },

    async close() {
      await context.close().catch(() => undefined);
      await browser.close().catch(() => undefined);
    },
  };
}
