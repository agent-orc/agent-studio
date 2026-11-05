import AxeBuilder from '@axe-core/playwright';
import { expect, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const ASSERTED_RULES = ['button-name', 'aria-prohibited-attr', 'aria-hidden-focus', 'label'] as const;

/** Run every axe rule, report the full result, and gate the agreed audit rules. */
export async function checkAccessibility(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const result = await new AxeBuilder({ page }).analyze();
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

  for (const id of ASSERTED_RULES) {
    const nodes = result.violations.find((violation) => violation.id === id)?.nodes ?? [];
    expect(nodes.map((node) => ({ target: node.target, failureSummary: node.failureSummary })),
      `${name}: ${id}`).toEqual([]);
  }
}
