import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';

/** Optional workspace-only axe audit. The package is kept outside the checkout. */
export async function auditModal(page: Page, selector: string, name: string): Promise<void> {
  const script = process.env.AXE_CORE_PATH;
  const results = process.env.JOB_RESULTS_DIR;
  if (!script || !results) return;
  await page.addScriptTag({ path: script });
  const audit = await page.evaluate(async target => {
    const axe = (window as unknown as { axe: {
      run: (element: Element) => Promise<{ violations: Array<{
        id: string; impact: string | null; description: string;
        nodes: Array<{ target: string[]; failureSummary: string | null }>;
      }> }>;
    } }).axe;
    return axe.run(document.querySelector(target)!);
  }, selector);
  writeFileSync(join(results, `axe-${name}.json`), JSON.stringify({
    dialog: selector,
    violations: audit.violations.map(({ id, impact, description, nodes }) => ({
      id, impact, description, nodes: nodes.map(({ target, failureSummary }) => ({ target, failureSummary })),
    })),
  }, null, 2));
}
