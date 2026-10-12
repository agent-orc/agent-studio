#!/usr/bin/env node
// Reduce Playwright's JSON reporter to a small per-file timing and flake report.
import { readFileSync, writeFileSync } from 'node:fs';

const [input, output] = process.argv.slice(2);
if (!input || !output) throw new Error('Usage: node summarize-playwright.mjs <report.json> <summary.json>');
const report = JSON.parse(readFileSync(input, 'utf8'));
const files = new Map();

function visit(suite) {
  for (const spec of suite.specs ?? []) {
    const file = spec.file ?? suite.file ?? '(unknown)';
    const entry = files.get(file) ?? { file, tests: 0, durationMs: 0, failed: 0, flaky: 0, skipped: 0 };
    for (const test of spec.tests ?? []) {
      entry.tests++;
      entry.durationMs += (test.results ?? []).reduce((sum, item) => sum + (item.duration ?? 0), 0);
      if (test.status === 'unexpected') entry.failed++;
      if (test.status === 'flaky') entry.flaky++;
      if (test.status === 'skipped') entry.skipped++;
    }
    files.set(file, entry);
  }
  for (const child of suite.suites ?? []) visit(child);
}
for (const suite of report.suites ?? []) visit(suite);
const summary = {
  stats: report.stats,
  specFiles: files.size,
  files: [...files.values()].sort((a, b) => b.durationMs - a.durationMs),
};
writeFileSync(output, JSON.stringify(summary, null, 2) + '\n');
