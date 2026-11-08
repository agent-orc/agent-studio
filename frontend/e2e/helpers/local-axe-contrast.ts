import type { Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

/** Optional workspace-only axe audit; axe-core is deliberately not a package dependency. */
export async function captureLocalAxeContrast(page: Page, surface: string, theme: string): Promise<void> {
  if (process.env.CONTRAST_AXE_LOCAL !== '1') return;
  await page.addScriptTag({ path: join(process.cwd(), 'node_modules/axe-core/axe.min.js') });
  const result = await page.evaluate(async () => {
    const axe = (window as unknown as { axe: {
      run(root: Document, options: object): Promise<{
        passes: { id: string; nodes: unknown[] }[];
        violations: { id: string; impact: string | null; nodes: { target: string[]; failureSummary?: string }[] }[];
      }>;
    } }).axe;
    const report = await axe.run(document, { runOnly: { type: 'rule', values: ['color-contrast'] } });
    return {
      passCount: report.passes.reduce((total, rule) => total + rule.nodes.length, 0),
      violations: report.violations.map(rule => ({
        id: rule.id, impact: rule.impact,
        nodes: rule.nodes.map(node => ({ target: node.target, failureSummary: node.failureSummary })),
      })),
    };
  });
  const dir = process.env.JOB_RESULTS_DIR ?? 'test-results';
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, `axe-${surface}-${theme}.json`), JSON.stringify(result, null, 2) + '\n');
}
