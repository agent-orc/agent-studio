import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme, type Theme } from '../helpers/theme';

/** Website scenes over the isolated, pinned ADR-0056 demo installation. */
test.describe.configure({ mode: 'serial' });
test.use({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 2, reducedMotion: 'reduce' });

const root = path.resolve(__dirname, '..', '..', '..');
const out = path.join(root, 'docs/assets/images/presentation');
const reviewOut = process.env.JOB_RESULTS_DIR;
const seed = JSON.parse(fs.readFileSync(path.join(root, 'scripts/presentation-capture/pinned-seed.json'), 'utf8')) as {
  projects: { name: string }[];
  operatorScenes: { id: string; surface: string; heading: string; lines: string[]; note: string }[];
};
const card = '[data-testid="task-card"], [data-testid="job-card"]';

async function openDemo(page: Page, id: string): Promise<void> {
  await page.clock.setFixedTime('2026-08-09T13:00:00.000Z');
  await page.goto('/');
  await page.addStyleTag({ content: 'body::before,.dev-banner,[data-testid="dev-banner"],[role="tooltip"]{display:none!important}[data-testid="status-bar"] .statusbar__quota{visibility:hidden!important}' });
  const picker = page.getByTestId('studio-project-picker-trigger');
  await expect(picker).toBeVisible({ timeout: 20_000 });
  await picker.click();
  const panel = page.getByTestId('studio-project-picker-panel');
  for (const project of seed.projects) await expect(panel.getByText(project.name, { exact: true })).toBeVisible();
  await panel.getByText('Demo App', { exact: true }).click();
  await expect(page.locator(card).filter({ hasText: 'DEMO-9' }).first()).toBeVisible();
  if (id === 'timeline' || id === 'attention-lease') {
    const target = page.locator(card).filter({ hasText: 'DEMO-9' }).first();
    await target.scrollIntoViewIfNeeded();
    await target.click();
    await expect(page.getByTestId('pane-protocol')).toBeVisible();
    await page.getByTestId(id === 'timeline' ? 'prompt-tab-timeline' : 'prompt-tab-evidence').click();
    if (id === 'timeline') {
      await expect(page.getByTestId('timeline-tab')).toBeVisible();
      await expect(page.getByTestId('timeline-event').first()).toBeVisible();
    } else {
      await expect(page.getByTestId('evidence-view')).toBeVisible();
    }
  }
  if (id === 'operator-log-chat') {
    await page.getByTestId('studio-titlebar-chat').click();
    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();
    await expect(sheet).toContainText('What did the operator do overnight?');
    await expect(page.getByTestId('orch-answer-context-receipt')).toBeVisible();
  }
  await page.mouse.move(1, 1);
}

async function showScenario(page: Page, scene: (typeof seed.operatorScenes)[number], theme: Theme): Promise<void> {
  await page.evaluate(({ scene, theme }) => {
    document.getElementById('pinned-operator-scenario')?.remove();
    const dark = theme === 'dark';
    const panel = document.createElement('aside');
    panel.id = 'pinned-operator-scenario';
    panel.setAttribute('aria-label', `Pinned demo scenario: ${scene.surface}`);
    const label = document.createElement('p');
    label.textContent = `PINNED DEMO SCENARIO · ${scene.surface}`;
    const heading = document.createElement('h2');
    heading.textContent = scene.heading;
    const list = document.createElement('ul');
    for (const line of scene.lines) {
      const item = document.createElement('li');
      item.textContent = line;
      list.append(item);
    }
    const note = document.createElement('p');
    note.textContent = scene.note;
    panel.append(label, heading, list, note);
    const terminal = scene.surface.startsWith('$');
    Object.assign(panel.style, {
      position: 'fixed', top: scene.id === 'timeline' ? '480px' : '180px',
      right: terminal || scene.id === 'operator-log-chat' || scene.id === 'timeline' ? '900px' : '70px',
      width: '760px', zIndex: '2147483646',
      padding: '36px 44px', border: `1px solid ${dark ? '#535a66' : '#c5cbd4'}`,
      borderRadius: '20px', background: dark ? '#20242b' : '#f9fafb', color: dark ? '#f5f6f8' : '#20242b',
      boxShadow: '0 18px 56px rgba(0,0,0,.22)', font: terminal ? '18px/1.5 ui-monospace, monospace' : '18px/1.5 system-ui, sans-serif',
    });
    Object.assign(label.style, { margin: '0 0 16px', fontSize: '14px', fontWeight: '700', letterSpacing: '.09em', color: dark ? '#a9c8fb' : '#245b9c' });
    Object.assign(heading.style, { margin: '0 0 22px', fontSize: '34px', lineHeight: '1.2', letterSpacing: '-.02em' });
    Object.assign(list.style, { margin: '0 0 24px', paddingLeft: terminal ? '0' : '25px', listStyle: terminal ? 'none' : 'disc', fontSize: '22px', lineHeight: '1.7' });
    for (const item of list.children) Object.assign((item as HTMLElement).style, { paddingBottom: '8px' });
    Object.assign(note.style, { margin: '0', paddingTop: '16px', borderTop: `1px solid ${dark ? '#535a66' : '#c5cbd4'}`, fontSize: '15px', color: dark ? '#b7c0cc' : '#52606e' });
    document.body.append(panel);
  }, { scene, theme });
  await expect(page.locator('#pinned-operator-scenario')).toContainText(scene.heading);
}

for (const [index, scene] of seed.operatorScenes.entries()) {
  test(`operator website still ${String(index + 9).padStart(2, '0')} - ${scene.id}`, async ({ page }, info: TestInfo) => {
    expect(process.env.PW_VISUAL_CAPTURE ?? 'marketing').toBe('marketing');
    await openDemo(page, scene.id);
    for (const theme of ['light', 'dark'] as const satisfies readonly Theme[]) {
      await setTheme(page, theme);
      await showScenario(page, scene, theme);
      const name = `${String(index + 9).padStart(2, '0')}-operator-${scene.id}--${theme}--pinned.png`;
      const file = path.join(out, name);
      fs.mkdirSync(out, { recursive: true });
      await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
      const png = await page.screenshot({ path: file, animations: 'disabled', caret: 'hide', scale: 'device' });
      expect(png.readUInt32BE(16)).toBe(3840);
      expect(png.readUInt32BE(20)).toBe(2160);
      if (reviewOut) {
        fs.mkdirSync(reviewOut, { recursive: true });
        fs.copyFileSync(file, path.join(reviewOut, name));
      }
      await info.attach(name, { path: file, contentType: 'image/png' });
    }
  });
}
