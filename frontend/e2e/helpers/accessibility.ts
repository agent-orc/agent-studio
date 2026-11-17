import AxeBuilder from '@axe-core/playwright';
import { expect, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

/** WCAG 2 A/AA, 2.1 A/AA and 2.2 AA, the audit scope of AGT-3796. */
export const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] as const;
const BLOCKING_IMPACTS = new Set(['critical', 'serious']);

/** Run the WCAG rules, report every result, and gate critical and serious issues. */
export async function checkAccessibility(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const result = await new AxeBuilder({ page }).withTags([...WCAG_TAGS]).analyze();
  const report = {
    name,
    url: result.url,
    violations: result.violations.map(({ id, impact, nodes }) => ({
      id, impact, nodes: nodes.map(({ target, failureSummary }) => ({ target, failureSummary })),
    })),
  };
  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) {
    fs.mkdirSync(resultsDir, { recursive: true });
    fs.writeFileSync(path.join(resultsDir, `axe-${name}.json`), JSON.stringify(report, null, 2));
  }
  await testInfo.attach(`axe-${name}`, { body: JSON.stringify(report, null, 2), contentType: 'application/json' });

  const blocking = report.violations
    .filter((violation) => BLOCKING_IMPACTS.has(violation.impact ?? ''))
    .map(({ id, impact, nodes }) => ({ id, impact, nodes }));
  expect.soft(blocking, `${name}: critical or serious axe issues`).toEqual([]);
}
