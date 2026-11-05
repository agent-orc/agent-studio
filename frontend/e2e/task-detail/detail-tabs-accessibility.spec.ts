import { expect, test } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, waitForJob } from '../helpers/jobs';

const stage = process.env['AGT_3531_STAGE'] === 'before' ? 'before' : 'after';
const output = process.env['JOB_RESULTS_DIR'] ?? 'test-results';
const statusMarkdown = '# Status\n\n- Result: Success\n\n## Overview\n\n- Problem: Detail tabs need to remain reachable.\n- Solution: Manual keyboard tabs and mobile scrolling.\n';

for (const width of [390, 1280] as const) {
  for (const theme of ['light', 'dark'] as const) {
    test(`detail tabs work at ${width} px in ${theme} theme`, async ({ page }) => {
      test.setTimeout(120_000);
      const paths = await api<{ path: string }[]>('/api/watch-paths');
      const watchPath = paths[0]?.path;
      if (!watchPath) throw new Error('No watch path configured');
      const job = await createJob({
        title: `detail-tabs-accessibility-${Date.now()}`,
        watchPath,
        promptMarkdown: '# Detail tabs accessibility fixture',
        targetState: '2-ready',
      });
      mkdirSync(output, { recursive: true });

      try {
        await waitForJob(job.id, watchPath, () => true);
        // The task API only permits prompt.md edits. Supply the result and
        // long decision summary through the browser response instead of
        // writing directly into the task workspace.
        await page.route(url => url.pathname.startsWith('/api/v1/projects/')
          && url.pathname.endsWith(`/tasks/${encodeURIComponent(job.id)}`), async route => {
          const response = await route.fetch();
          const detail = await response.json();
          await route.fulfill({ response, json: {
            ...detail,
            info: {
              ...detail.info,
              needsInput: {
                message: Array.from({ length: 14 }, (_, index) => `Decision context ${index + 1}: review the current task before choosing an answer.`).join('\n'),
                firstLine: 'Decision context 1: review the current task before choosing an answer.',
                runAttemptId: '1',
                salvageBranch: null,
                artifactPath: 'results/question.md',
              },
            },
            statusMarkdown,
          } });
        });
        await page.route(url => url.pathname.endsWith(`/tasks/${encodeURIComponent(job.id)}/details/documents`)
          && url.searchParams.get('name') === 'status', async route => {
          const response = await route.fetch();
          const document = await response.json();
          await route.fulfill({ response, json: {
            ...document,
            data: { ...document.data, markdown: statusMarkdown },
          } });
        });
        await page.setViewportSize({ width, height: 900 });
        await page.addInitScript(value => localStorage.setItem('atp.studio.theme', value), theme);
        const detailUrl = `/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`;
        // The index can briefly lag the core after fixture creation. Retry
        // the deep link if the welcome view wins that first restore race.
        await expect(async () => {
          await page.goto(detailUrl);
          await expect(page.getByTestId('prompt-tab-overview')).toBeVisible({ timeout: 8_000 });
        }).toPass({ timeout: 45_000 });

        const tabs = [
          { name: 'overview', testid: 'prompt-tab-overview' },
          { name: 'task', testid: 'inspector-tab-task' },
          { name: 'activity', testid: 'inspector-tab-activity' },
          { name: 'result', testid: 'inspector-tab-protocol' },
        ];
        for (const tab of tabs) {
          const control = page.getByTestId(tab.testid);
          await expect(control).toBeAttached();
          if (stage === 'before') {
            const box = await control.boundingBox();
            let normalClick = 'succeeded';
            try {
              await control.click({ timeout: 1500 });
            } catch (error) {
              normalClick = `failed: ${String(error).split('\n')[0]}`;
            }
            writeFileSync(join(output, `detail-tabs-before-${width}-${theme}-${tab.name}.json`), JSON.stringify({ box, normalClick }, null, 2));
            await control.evaluate(element => (element as HTMLButtonElement).click());
          } else {
            await control.click();
            await expect(control).toHaveAttribute('aria-selected', 'true');
            const panelId = await control.getAttribute('aria-controls');
            expect(panelId).toBeTruthy();
            await expect(page.locator(`#${panelId}`)).toHaveAttribute('aria-labelledby', await control.getAttribute('id'));
            await expect(control.locator('..')).toHaveAttribute('role', 'tablist');
            await control.evaluate(element => element.closest('header')?.scrollIntoView({ block: 'start' }));
          }
          await page.screenshot({ path: join(output, `detail-tabs-${stage}-${width}-${theme}-${tab.name}.png`), animations: 'disabled' });
        }

        if (stage === 'after') {
          const task = page.getByTestId('inspector-tab-task');
          const activity = page.getByTestId('inspector-tab-activity');
          await task.click();
          await task.focus();
          await page.keyboard.press('ArrowRight');
          await expect(activity).toBeFocused();
          await expect(task).toHaveAttribute('aria-selected', 'true');
          await page.keyboard.press('Enter');
          await expect(activity).toHaveAttribute('aria-selected', 'true');
          await page.keyboard.press('ArrowLeft');
          await expect(task).toBeFocused();
          await page.keyboard.press('Space');
          await expect(task).toHaveAttribute('aria-selected', 'true');
          const prompt = page.getByTestId('prompt-tab-overview');
          await prompt.focus();
          await page.keyboard.press('ArrowRight');
          await expect(page.getByTestId('prompt-tab-timeline')).toBeFocused();

          if (width === 1280) {
            // The focused tab may move into the overflow menu when the pane
            // narrows. An inline tab must still be reachable by Tab.
            const timeline = page.getByTestId('prompt-tab-timeline');
            await timeline.focus();
            const promptTablist = page.getByTestId('pane-prompt-header').getByRole('tablist');
            await promptTablist.locator('..').evaluate(element => {
              (element as HTMLElement).style.flex = '0 0 96px';
              (element as HTMLElement).style.maxWidth = '96px';
            });
            await expect(timeline).toHaveCount(0);
            const tabStop = promptTablist.locator(':scope > [role="tab"][tabindex="0"]');
            await expect(tabStop).toHaveCount(1);
            await page.getByTestId('pane-prompt-header').getByTestId('pane-tabs-overflow').focus();
            await page.keyboard.press('Shift+Tab');
            await expect(tabStop).toBeFocused();
          }
        }

        const axeScript = process.env['AGT_3531_AXE_SCRIPT'];
        if (axeScript) {
          await page.addScriptTag({ path: axeScript });
          const audit = await page.evaluate(async () => {
            const axe = (window as Window & { axe?: { run: (root: Document) => Promise<{
              violations: { id: string; impact: string | null; nodes: { target: string[]; failureSummary: string | null }[] }[];
              passes: unknown[];
              incomplete: unknown[];
            }> } }).axe;
            if (!axe) throw new Error('axe-core was not loaded');
            const result = await axe.run(document);
            return {
              violations: result.violations.map(item => ({ id: item.id, impact: item.impact, nodes: item.nodes.map(node => ({ target: node.target, failureSummary: node.failureSummary })) })),
              passedRules: result.passes.length,
              incompleteRules: result.incomplete.length,
            };
          });
          writeFileSync(join(output, `detail-tabs-axe-${stage}-${width}-${theme}.json`), JSON.stringify(audit, null, 2));
          if (stage === 'after') {
            expect(audit.violations.filter(item => item.id === 'aria-required-children')).toEqual([]);
          }
        }
      } finally {
        await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
      }
    });
  }
}
