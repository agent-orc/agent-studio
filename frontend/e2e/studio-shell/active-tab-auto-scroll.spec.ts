import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
  escalated: [], completed: [], archive: [],
};

const HUB_TABS = Array.from({ length: 9 }, (_, index) => ({
  kind: 'hub',
  projectName: `Long project ${index + 1}`,
  section: 'overview',
}));

type StoredTab = Record<string, unknown>;

async function bootWithTabs(page: Page, tabs: StoredTab[], activeKey: string): Promise<void> {
  await page.setViewportSize({ width: 900, height: 700 });
  await page.addInitScript(() => {
    const calls: { key?: string; options?: ScrollIntoViewOptions }[] = [];
    Object.defineProperty(window, '__studioTabScrollCalls', { value: calls });
    const nativeScrollIntoView = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = function (options?: boolean | ScrollIntoViewOptions): void {
      calls.push({
        key: (this as HTMLElement).dataset['tabKey'],
        options: typeof options === 'object' ? options : undefined,
      });
      nativeScrollIntoView.call(this, options);
    };
  });
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
    if (url.includes('/api/v1/studio/auth/status')) {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/v1/studio/board')) return json(EMPTY_GROUPED);
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    if (/\/api\/tasks(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/watch-paths')) {
      return json(HUB_TABS.map((tab, index) => ({
        name: tab.projectName,
        path: `/mock/project-${index + 1}`,
      })));
    }
    if (url.includes('/api/v1/workspaces') || url.includes('/api/workspaces')) return json([]);
    return route.continue();
  });
  await page.addInitScript(({ storedTabs, storedActiveKey }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: storedTabs,
      activeKey: storedActiveKey,
    }));
  }, { storedTabs: tabs, storedActiveKey: activeKey });

  await page.goto('/');
  await expect(page.getByTestId('studio-tab-list')).toBeVisible({ timeout: 30_000 });
}

async function expectInsideStrip(tab: Locator, strip: Locator): Promise<void> {
  await expect.poll(async () => {
    const [tabBox, stripBox] = await Promise.all([tab.boundingBox(), strip.boundingBox()]);
    if (!tabBox || !stripBox) return false;
    return tabBox.x >= stripBox.x - 1
      && tabBox.x + tabBox.width <= stripBox.x + stripBox.width + 1;
  }).toBe(true);
}

async function expectSmoothNearestScroll(page: Page, key: string): Promise<void> {
  await expect.poll(() => page.evaluate(expectedKey => {
    const calls = (window as typeof window & {
      __studioTabScrollCalls?: { key?: string; options?: ScrollIntoViewOptions }[];
    }).__studioTabScrollCalls ?? [];
    return calls.some(call => call.key === expectedKey
      && call.options?.behavior === 'smooth'
      && call.options.block === 'nearest'
      && call.options.inline === 'nearest');
  }, key)).toBe(true);
}

async function resetScrollCalls(page: Page): Promise<void> {
  await page.evaluate(() => {
    const calls = (window as typeof window & {
      __studioTabScrollCalls?: { key?: string; options?: ScrollIntoViewOptions }[];
    }).__studioTabScrollCalls;
    calls?.splice(0);
  });
}

async function scrollCallCount(page: Page): Promise<number> {
  return page.evaluate(() => (window as typeof window & {
    __studioTabScrollCalls?: { key?: string; options?: ScrollIntoViewOptions }[];
  }).__studioTabScrollCalls?.length ?? 0);
}

async function captureStrip(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const fileName = `${name}--mocked.png`;
  const path = process.env.JOB_RESULTS_DIR
    ? `${process.env.JOB_RESULTS_DIR}/${fileName}`
    : testInfo.outputPath(fileName);
  await page.getByTestId('studio-tabbar').screenshot({ path });
  await testInfo.attach(name, { path, contentType: 'image/png' });
}

test.describe('studio-shell · active tab remains visible in the scrolling strip', () => {
  test('activation of an off-screen tab scrolls it into view', async ({ page }, testInfo) => {
    const settings = { kind: 'workspace-settings' };
    const tabs = [settings, ...HUB_TABS];
    await bootWithTabs(page, tabs, 'hub:Long project 9');

    await expectInsideStrip(
      page.getByTestId('studio-tab-hub:Long project 9'),
      page.getByTestId('studio-tab-list'),
    );
    await resetScrollCalls(page);

    await page.getByTestId('studio-ab-settings').click();

    const active = page.getByTestId('studio-tab-workspace-settings');
    await expect(active).toHaveAttribute('aria-selected', 'true');
    await expectSmoothNearestScroll(page, 'workspace-settings');
    await expectInsideStrip(active, page.getByTestId('studio-tab-list'));
    await captureStrip(page, testInfo, 'active-tab-off-screen-visible');
  });

  test('activation of a visible tab does not scroll', async ({ page }, testInfo) => {
    const settings = { kind: 'workspace-settings' };
    await bootWithTabs(page, [settings, ...HUB_TABS], 'hub:Long project 1');
    await resetScrollCalls(page);

    const active = page.getByTestId('studio-tab-workspace-settings');
    await active.click();
    await expect(active).toHaveAttribute('aria-selected', 'true');
    await page.evaluate(() => new Promise<void>(resolve => {
      requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
    }));

    expect(await scrollCallCount(page)).toBe(0);
    await captureStrip(page, testInfo, 'active-tab-already-visible');
  });
});
