import { expect, test, type Locator, type Page } from '@playwright/test';
import { fullMocks, mockApp, openHub, PROJECT } from '../helpers/project-hub-mocks';

/**
 * Viewport keys scroll the main scroll container with nothing focused (AGT-4033).
 *
 * The studio document does not scroll; the view's own container does. Before the
 * fix a freshly loaded view had no focus inside that container, so ArrowDown,
 * PageDown, Space, Home and End did nothing until a control in it was clicked.
 * Also guards that no global handler takes an arrow key without scrolling.
 */

interface ViewCase { name: string; section: string; readyTestId: string; deployTab?: string }
const VIEWS: ViewCase[] = [
  { name: 'Deploy overview', section: 'deploy', readyTestId: 'project-deploy-area' },
  { name: 'Deploy history', section: 'deploy', readyTestId: 'project-deploy-area', deployTab: 'history' },
  { name: 'Operations', section: 'operations', readyTestId: 'project-operations-environments' },
];

/** Scroll offset of the largest scrolled-or-scrollable element in the main region. */
const scrollOffsets = (page: Page) => page.evaluate(() => {
  const main = document.querySelector<HTMLElement>('[data-testid="studio-main"]')!;
  return [main, ...Array.from(main.querySelectorAll<HTMLElement>('*'))]
    .filter(element => element.scrollHeight > element.clientHeight + 1 && /auto|scroll/.test(getComputedStyle(element).overflowY))
    .reduce((sum, element) => sum + element.scrollTop, 0);
});

/** Records every arrow keydown that reaches the window already default-prevented. */
async function trackPrevented(page: Page): Promise<void> {
  await page.evaluate(() => {
    const w = window as unknown as { __prevented: string[] };
    w.__prevented = [];
    window.addEventListener('keydown', event => {
      if (event.defaultPrevented) w.__prevented.push(event.key);
    });
  });
}

async function openView(page: Page, view: ViewCase): Promise<void> {
  await openHub(page, fullMocks(), view.section, view.readyTestId);
  if (view.deployTab) {
    await page.getByTestId(`deploy-tab-${view.deployTab}`).click();
    await expect(page.getByTestId('deploy-run-open-run-c-production')).toBeVisible();
  }
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await page.mouse.move(0, 0);
}

for (const view of VIEWS) {
  test.describe(view.name, () => {
    for (const key of ['ArrowDown', 'PageDown', 'Space', 'End']) {
      test(`${key} scrolls with nothing focused`, async ({ page }) => {
        await openView(page, view);
        expect(await page.evaluate(() => document.activeElement === document.body)).toBe(true);
        const before = await scrollOffsets(page);
        await trackPrevented(page);
        await page.keyboard.press(key);
        await expect.poll(() => scrollOffsets(page)).toBeGreaterThan(before);
      });
    }

    test('Home and ArrowUp return to the top', async ({ page }) => {
      await openView(page, view);
      await page.keyboard.press('End');
      await expect.poll(() => scrollOffsets(page)).toBeGreaterThan(0);
      await page.keyboard.press('ArrowUp');
      const afterUp = await scrollOffsets(page);
      await page.keyboard.press('Home');
      await expect.poll(() => scrollOffsets(page)).toBeLessThanOrEqual(afterUp);
      await expect.poll(() => scrollOffsets(page)).toBe(0);
    });

    test('a global handler does not take arrow keys without scrolling', async ({ page }) => {
      await openView(page, view);
      await trackPrevented(page);
      for (const key of ['ArrowDown', 'ArrowUp', 'ArrowDown']) {
        const before = await scrollOffsets(page);
        await page.keyboard.press(key);
        await page.waitForTimeout(100);
        const prevented = await page.evaluate(() => (window as unknown as { __prevented: string[] }).__prevented.splice(0));
        if (prevented.length > 0) expect(await scrollOffsets(page), `${key} was prevented without scrolling`).not.toBe(before);
      }
    });
  });
}

test('arrow keys in a text field are left to the field', async ({ page }) => {
  await openView(page, VIEWS[0]);
  await page.evaluate(() => {
    const input = document.createElement('input');
    input.setAttribute('data-testid', 'probe-input');
    document.querySelector('[data-testid="project-deploy-area"]')!.prepend(input);
    input.focus();
  });
  const before = await scrollOffsets(page);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('End');
  await page.waitForTimeout(100);
  expect(await scrollOffsets(page)).toBe(before);
});

async function expectViewportKeys(page: Page, owner: Locator): Promise<void> {
  await expect(owner).toBeVisible();
  const offset = () => owner.evaluate(element => element.scrollTop);
  await expect.poll(() => owner.evaluate(element => element.scrollHeight - element.clientHeight)).toBeGreaterThan(1);
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await expect(page.locator('body')).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect.poll(offset).toBeGreaterThan(0);
  const afterDown = await offset();
  await page.keyboard.press('ArrowUp');
  await expect.poll(offset).toBeLessThan(afterDown);
  await page.keyboard.press('PageDown');
  await expect.poll(offset).toBeGreaterThan(0);
  const afterPageDown = await offset();
  await page.keyboard.press('PageUp');
  await expect.poll(offset).toBeLessThan(afterPageDown);
  const afterPageUp = await offset();
  await page.keyboard.press('Space');
  await expect.poll(offset).toBeGreaterThan(afterPageUp);
  await page.keyboard.press('End');
  await expect.poll(() => owner.evaluate(element =>
    Math.abs(element.scrollHeight - element.clientHeight - element.scrollTop))).toBeLessThanOrEqual(1);
  await page.keyboard.press('Home');
  await expect.poll(offset).toBe(0);
}

test('board viewport keys scroll the group containing the ready cards', async ({ page }) => {
  await mockApp(page, fullMocks());
  const watchPath = '/srv/tasks/website';
  const ready = Array.from({ length: 30 }, (_, index) => {
    const id = `WEB-${index + 1}`;
    return { id, key: id, taskKey: `${watchPath}::${id}`, title: `Ready card ${index + 1}`, state: '2-ready',
      projectName: PROJECT, watchPath, folderPath: `${watchPath}/2-ready/${id}`, createdAt: '2026-11-27T12:00:00Z',
      cliType: 'codex', agent: 'codex', commits: [], tags: [] };
  });
  await page.route('**/api/v1/studio/board**', route => route.fulfill({ json: {
    ready, backlog: [], preparation: [], orchestratorPrep: [], progress: [], failedPickup: [], codeNotComplete: [],
    review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
  } }));
  await page.route(url => url.pathname === '/api/tasks', route => route.fulfill({ json: ready }));
  await page.goto('/#/board');
  const readyCard = page.getByTestId('task-card').filter({ hasText: ready[0].title });
  const owner = page.getByTestId(/^lane-group-lanes-/).filter({ has: readyCard });
  await expectViewportKeys(page, owner);
});

test('Releases viewport keys scroll the release history', async ({ page }) => {
  await mockApp(page, fullMocks());
  const pending = Array.from({ length: 40 }, (_, index) => ({
    key: `WEB-${index + 1}`, title: `Pending delivery ${index + 1}`, type: 'bug',
    href: `#/tasks/WEB-${index + 1}`, integratedAtUtc: null,
  }));
  await page.route('**/studio-releases', route => route.fulfill({ json: {
    project: PROJECT, runningRevision: 'a'.repeat(40), computedAtUtc: '2026-11-27T12:00:00Z',
    detail: null, pending, releases: [],
  } }));
  await page.goto('/#/projects/PROJ-WEB/studio-releases');
  await expect(page.getByTestId('studio-release-pending')).toHaveCount(pending.length);
  await expectViewportKeys(page, page.locator('app-studio-releases'));
});
