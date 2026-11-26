/**
 * Theme + legibility helpers shared by the workspace-overlay specs.
 *
 * The overlays (executive summary, CLI-usage timeline, visual-evidence reel)
 * all paint on a panel that flips with `data-studio-theme`. Their content must
 * stay WCAG-AA legible on BOTH themes, so the specs:
 *   1. stamp the theme (`setTheme`),
 *   2. brush aside the dev-only NG0919 error dialog (`dismissDevErrorDialog`),
 *   3. sample an element's effective fg/bg (`sampleColours`) and feed the pair
 *      to `contrastRatio` from `./contrast`.
 *
 * `sampleColours` normalises everything to plain `rgb()` / `rgba()` strings —
 * including translucent `color-mix(...)` chips that Chromium serialises as
 * `color(srgb r g b / a)` — so the integer-only parser in `./contrast` copes.
 */
import { expect, type Page } from '@playwright/test';

export type Theme = 'dark' | 'light';

/**
 * Stamp `data-studio-theme` on <html> and persist the preference so the
 * shell's theme effect doesn't overwrite it on the next change-detection.
 */
export async function setTheme(page: Page, theme: Theme): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

/**
 * A dev-only NG0919 global error dialog (a circular-dep artifact that only
 * appears under `ng serve`, never in the stable/prod build) can paint over an
 * overlay and intercept pointer events. Dismiss it if present so it neither
 * blocks interaction nor pollutes a screenshot. No-op on stable/prod.
 */
export async function dismissDevErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.keyboard.press('Escape');
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => { /* best effort */ });
  }
}

/**
 * Sample an element's computed text colour and the *effective* opaque
 * background painted behind it. Translucent tints (chips, pills) are
 * composited from the element up onto the theme backdrop so the returned
 * `bg` is the surface the eye actually sees. Both come back as plain
 * `rgba()` / `rgb()` strings ready for `contrastRatio`.
 */
export async function sampleColours(
  page: Page,
  selector: string,
  nth = 0,
): Promise<{ color: string; bg: string }> {
  return page.locator(selector).nth(nth).evaluate((el) => {
    type Rgba = [number, number, number, number];
    const parse = (v: string): Rgba | null => {
      let m = /rgba?\(\s*([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)(?:[ ,/]+([\d.]+))?\s*\)/.exec(v);
      if (m) return [+m[1], +m[2], +m[3], m[4] === undefined ? 1 : +m[4]];
      m = /color\(\s*srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)(?:\s*\/\s*([\d.]+))?\s*\)/.exec(v);
      if (m) return [Math.round(+m[1] * 255), Math.round(+m[2] * 255), Math.round(+m[3] * 255), m[4] === undefined ? 1 : +m[4]];
      if (v === 'transparent') return [0, 0, 0, 0];
      return null;
    };
    const over = (top: Rgba, bottom: Rgba): Rgba => {
      const a = top[3];
      return [
        Math.round(top[0] * a + bottom[0] * (1 - a)),
        Math.round(top[1] * a + bottom[1] * (1 - a)),
        Math.round(top[2] * a + bottom[2] * (1 - a)),
        1,
      ];
    };

    const fg = parse(getComputedStyle(el as Element).color) ?? [0, 0, 0, 1];

    const layers: Rgba[] = [];
    let node: Element | null = el as Element;
    while (node) {
      const bg = parse(getComputedStyle(node).backgroundColor);
      if (bg && bg[3] > 0) layers.push(bg);
      node = node.parentElement;
    }

    // Backstop: the theme backdrop, so a fully-translucent chain still resolves
    // to the surface the eye sees (white on light, near-black on dark).
    const light = document.documentElement.dataset['studioTheme'] === 'light';
    let base: Rgba = light ? [255, 255, 255, 1] : [24, 24, 37, 1];
    let start = layers.length - 1;
    for (let i = layers.length - 1; i >= 0; i--) {
      if (layers[i][3] >= 1) { base = layers[i]; start = i - 1; break; }
    }
    let acc = base;
    for (let i = start; i >= 0; i--) acc = over(layers[i], acc);

    return {
      color: `rgba(${fg[0]}, ${fg[1]}, ${fg[2]}, ${fg[3]})`,
      bg: `rgb(${acc[0]}, ${acc[1]}, ${acc[2]})`,
    };
  });
}

/** Switch the real shell theme, including its responsive More-menu destination. */
export async function setThemeThroughControl(page: Page, theme: Theme): Promise<void> {
  if (await page.locator('html').getAttribute('data-studio-theme') !== theme) {
    const button = page.getByTestId('studio-titlebar-theme');
    if (await button.isVisible()) await button.click();
    else {
      await page.getByTestId('cockpit-header-more').click();
      await page.getByRole('menuitem', { name: 'Switch theme', exact: true }).click();
    }
  }
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
}
