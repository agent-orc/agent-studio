#!/usr/bin/env node
// Prints the readiness report the server computes about itself (AGT-3412) for
// move-studio-to-server.sh. The server owns every check; this module only
// lists them, failing lines first, each with what it means and its remedy.
// Usage: node readiness-report.mjs <report.json>; exit 4 when a check fails.
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const rank = { fail: 0, warn: 1, unknown: 2, pass: 3 };
const label = { fail: 'FAIL', warn: 'WARN', unknown: 'UNKNOWN', pass: 'ok' };

export function formatReadiness(report) {
  if (!report || !report.computedAt) {
    return { text: 'readiness: the server has not computed its readiness report yet; open the operations page and Recheck', failing: 0 };
  }
  const checks = Array.isArray(report.checks) ? report.checks : [];
  const failing = checks.filter(check => check.state === 'fail').length;
  const attention = checks.filter(check => check.state === 'warn' || check.state === 'unknown').length;
  const problems = checks
    .filter(check => check.state !== 'pass')
    .sort((a, b) => (rank[a.state] ?? 2) - (rank[b.state] ?? 2));
  const lines = [problems.length
    ? `readiness: ${failing} failing, ${attention} need attention, ${checks.length} checks (computed ${report.computedAt})`
    : `readiness: all ${checks.length} checks pass (computed ${report.computedAt})`];
  for (const check of problems) {
    const subject = check.subject ? ` [${check.subject}]` : '';
    lines.push(`  ${label[check.state] ?? check.state.toUpperCase()} ${check.id}${subject}: ${check.title}`);
    lines.push(`       ${check.meaning}`);
    if (check.remedy) lines.push(`       remedy: ${check.remedy}`);
    // AGT-3580: a config entry is text for a file, never a `$` line to run.
    if (check.configEntry) {
      lines.push(`       add to ${check.configEntry.file} under ${check.configEntry.section}:`);
      for (const entry of check.configEntry.text.split('\n').filter(Boolean)) lines.push(`           ${entry}`);
    }
    // AGT-3580: the shell command travels apart from the prose remedy.
    for (const command of (check.command ?? '').split('\n').filter(Boolean)) lines.push(`         $ ${command}`);
  }
  return { text: lines.join('\n'), failing };
}

export function runCli(argv, { read = file => readFileSync(file, 'utf8'), write = text => process.stdout.write(text) } = {}) {
  const [file] = argv;
  if (!file) throw new Error('usage: readiness-report.mjs <report.json>');
  let report;
  try {
    report = JSON.parse(read(file));
  } catch (error) {
    write(`readiness: the report could not be read (${error.message})\n`);
    return 2;
  }
  const { text, failing } = formatReadiness(report);
  write(text + '\n');
  return failing ? 4 : 0;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  process.exitCode = runCli(process.argv.slice(2));
}
