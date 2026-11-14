// The browser half of the product scenario: one Chromium with one context per
// simulated person, so the product manager, the designer, the manager and the
// contributor each hold their own Studio session. Pages act on data-testid
// hooks of the product controls (AGT-3181) and save screenshots into the report
// directory. Playwright comes from the frontend checkout, so the scenario adds
// no dependency.
import { mkdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const FRONTEND = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../frontend');
const VIEWPORT = { width: 1440, height: 900 };
const NAVIGATION_TIMEOUT_MS = 60_000;
const ELEMENT_TIMEOUT_MS = 30_000;
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
 * Opens a browser for one run. Screenshots go to `<reportDir>/screenshots`;
 * returned paths are relative to the report so the HTML links work.
 */
export async function openBrowser({ studioUrl, reportDir, chromium }) {
  const engine = chromium ?? await loadChromium();
  const browser = await engine.launch({ headless: true });
  const shotDir = path.join(reportDir, 'screenshots');
  await mkdir(shotDir, { recursive: true });
  const studio = new URL(studioUrl);
  // A loopback Studio (local-demo.sh) has a per-run certificate; no network sits in between.
  const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(studio.hostname);
  const pages = new Map();
  let counter = 0;

  async function screenshot(page, person, name) {
    counter += 1;
    const file = `${String(counter).padStart(2, '0')}-${person}-${name}.png`;
    await page.screenshot({ path: path.join(shotDir, file), fullPage: false });
    return `screenshots/${file}`;
  }

  async function personPage(person) {
    if (pages.has(person)) return pages.get(person);
    const context = await browser.newContext({ viewport: VIEWPORT, ignoreHTTPSErrors: loopback });
    const page = await context.newPage();
    page.setDefaultNavigationTimeout(NAVIGATION_TIMEOUT_MS);
    page.setDefaultTimeout(ELEMENT_TIMEOUT_MS);
    const byTestId = testId => page.getByTestId(testId).first();
    const settle = () => page.waitForLoadState('networkidle', { timeout: SETTLE_MS }).catch(() => undefined);

    const api = {
      /** Signs in through the Studio sign-in form (auth-gate.html), as the person would. */
      async signIn({ login, password }) {
        await page.goto(new URL('/', studio).href, { waitUntil: 'domcontentloaded' });
        const gate = page.getByTestId('auth-gate');
        const user = gate.locator('input[name="username"]');
        await user.waitFor({ timeout: NAVIGATION_TIMEOUT_MS });
        await user.fill(login);
        await gate.locator('input[name="password"][autocomplete="current-password"]').fill(password);
        // Wait before the click: a fast answer would otherwise arrive unobserved.
        const answered = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/auth/login'),
          { timeout: NAVIGATION_TIMEOUT_MS });
        await gate.getByRole('button', { name: 'Sign in', exact: true }).click();
        const answer = await answered;
        if (!answer.ok()) throw new Error(`Studio refused the browser sign-in of ${login} (${answer.status()}).`);
        await settle();
      },

      /**
       * Opens a one-time sign-in link (/?invite=<token>): the sign-in gate shows
       * "Accept invitation", the person chooses a password and joins the project.
       */
      async redeemInvitation(link, password) {
        await page.goto(new URL(link, studio).href, { waitUntil: 'domcontentloaded' });
        const gate = page.getByTestId('auth-gate');
        const field = gate.locator('input[name="newPassword"]');
        await field.waitFor({ timeout: NAVIGATION_TIMEOUT_MS });
        await field.fill(password);
        const answered = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/invite/accept'),
          { timeout: NAVIGATION_TIMEOUT_MS });
        await gate.getByRole('button', { name: 'Join project', exact: true }).click();
        const answer = await answered;
        if (!answer.ok()) throw new Error(`Studio refused the one-time sign-in link (${answer.status()}).`);
        await settle();
      },

      /** Opens a Studio hash route such as /#/projects/PROJ-012/board. */
      async openStudio(route) {
        await page.goto(new URL(route, studio).href, { waitUntil: 'domcontentloaded' });
        await settle();
      },

      /** Opens a Project URL of the explorer: the live preview with the product controls beside it. */
      async openProjectUrl(projectName, urlId) {
        await page.goto(new URL('/#', studio).href, { waitUntil: 'domcontentloaded' });
        await settle();
        const project = page.getByTestId(`studio-explorer-project-${projectName}`);
        await project.waitFor();
        const row = page.getByTestId(`studio-explorer-project-url-${projectName}-${urlId}`);
        if (!await row.isVisible().catch(() => false)) await project.click();
        await row.click();
        await byTestId('product-feedback').waitFor();
        await settle();
      },

      async click(testId) { await byTestId(testId).click(); },
      async fill(testId, value) { await byTestId(testId).fill(value); },
      async select(testId, value) { await byTestId(testId).selectOption(value); },
      async clickButtonIn(testId, name) { await byTestId(testId).getByRole('button', { name, exact: true }).click(); },
      async clickText(text) { await page.getByText(text, { exact: true }).first().click(); },

      /** Waits up to `timeoutMs` for a data-testid; returns whether it shows and its text. */
      async testIdState(testId, timeoutMs = 15_000) {
        const element = byTestId(testId);
        const visible = await element.waitFor({ state: 'visible', timeout: timeoutMs }).then(() => true, () => false);
        return { visible, text: visible ? (await element.innerText()).replace(/\s+/g, ' ').trim().slice(0, 1000) : null };
      },

      /** The visible text of the page, for refusal messages. */
      async bodyText() {
        return (await page.locator('body').innerText()).replace(/\s+/g, ' ').trim().slice(0, 2000);
      },

      /** Natural size of an image, so a mark can be placed in its own coordinates. */
      async imageSize(testId) {
        const element = byTestId(testId);
        await element.waitFor();
        await page.waitForFunction(el => el.complete && el.naturalWidth > 0, await element.elementHandle());
        return element.evaluate(el => ({ width: el.naturalWidth, height: el.naturalHeight, src: el.getAttribute('src') }));
      },

      /** Drags a rectangle across an image; `box` is in fractions of the image (0..1). */
      async markImage(testId, box) {
        const element = byTestId(testId);
        await element.scrollIntoViewIfNeeded();
        const bounds = await element.boundingBox();
        if (!bounds) throw new Error(`${testId} is not visible.`);
        const at = (fx, fy) => [bounds.x + fx * bounds.width, bounds.y + fy * bounds.height];
        await page.mouse.move(...at(box.x, box.y));
        await page.mouse.down();
        await page.mouse.move(...at(box.x + box.width, box.y + box.height), { steps: 5 });
        await page.mouse.up();
      },

      /**
       * Waits for the next `method` request whose path ends with `suffix` while
       * `action` runs; returns its status, the request body as text (empty when
       * Chromium does not expose a multipart body with a file) and the answer.
       */
      async captureRequest(method, suffix, action) {
        const answered = page.waitForResponse(response => response.request().method() === method
          && new URL(response.url()).pathname.endsWith(suffix), { timeout: NAVIGATION_TIMEOUT_MS });
        await action();
        const response = await answered;
        const body = response.request().postDataBuffer()?.toString('latin1') ?? '';
        return { status: response.status(), body, answer: (await response.text().catch(() => '')).slice(0, 2000) };
      },

      /**
       * Opens an address at the given viewport and measures the first button
       * whose name matches `pattern`: its box in fractions of the viewport, a
       * CSS selector, and the computed style a designer would talk about.
       */
      async measureButton(address, pattern, viewport = VIEWPORT) {
        await page.setViewportSize(viewport);
        try {
          const response = await page.goto(address, { waitUntil: 'load' });
          const buttons = page.getByRole('button');
          const names = await buttons.evaluateAll(items =>
            items.map(item => (item.getAttribute('aria-label') || item.textContent || '').trim()));
          const index = names.findIndex(name => new RegExp(pattern, 'i').test(name));
          if (index < 0) return { status: response?.status() ?? null, found: false, buttons: names.filter(Boolean) };
          const measured = await buttons.nth(index).evaluate((button, size) => {
            const rect = button.getBoundingClientRect();
            const style = getComputedStyle(button);
            const selector = button.id ? `#${CSS.escape(button.id)}`
              : button.getAttribute('aria-label') ? `button[aria-label="${button.getAttribute('aria-label')}"]`
                : `${button.parentElement?.id ? `#${CSS.escape(button.parentElement.id)} > ` : ''}button:nth-of-type(${[...button.parentElement.children].filter(item => item.tagName === 'BUTTON').indexOf(button) + 1})`;
            return {
              box: { x: rect.left / size.width, y: rect.top / size.height, width: rect.width / size.width, height: rect.height / size.height },
              pixels: { width: Math.round(rect.width), height: Math.round(rect.height) },
              selector,
              className: button.className || null,
              style: { fontSize: style.fontSize, backgroundColor: style.backgroundColor, color: style.color, borderColor: style.borderColor, padding: style.padding },
            };
          }, viewport);
          return { status: response?.status() ?? null, found: true, name: names[index], buttons: names.filter(Boolean), ...measured };
        } finally {
          await page.setViewportSize(VIEWPORT);
        }
      },

      /** Opens any address and returns its status and visible text. */
      async openAddress(address) {
        const response = await page.goto(address, { waitUntil: 'load' });
        return { status: response?.status() ?? null, text: (await page.locator('body').innerText()).slice(0, 2000) };
      },

      screenshot(name) { return screenshot(page, person, name); },
      close: () => context.close().catch(() => undefined),
    };
    pages.set(person, api);
    return api;
  }

  return {
    page: personPage,
    async close() {
      for (const page of pages.values()) await page.close();
      await browser.close().catch(() => undefined);
    },
  };
}
