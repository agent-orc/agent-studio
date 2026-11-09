import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontendRoot = dirname(dirname(fileURLToPath(import.meta.url)));

function runAngular(args, reportPath) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [
      join(frontendRoot, 'node_modules/@angular/cli/bin/ng.js'),
      'test', 'frontend', '--watch=false', '--progress=false', ...args,
    ], {
      cwd: frontendRoot,
      env: { ...process.env, AGENT_STUDIO_VITEST_REPORT: reportPath },
      stdio: 'inherit',
      windowsHide: true,
    });
    child.once('error', reject);
    child.once('close', (code, signal) => resolve(signal ? 1 : code ?? 1));
  });
}

export function completedTestSummary(report) {
  const files = report?.testResults;
  if (!Array.isArray(files) || files.length === 0
      || !Number.isInteger(report.numTotalTests) || report.numTotalTests < 1
      || !Number.isInteger(report.numPassedTests) || report.numPassedTests < 1
      || report.numFailedTests > 0)
    return null;
  const passedFiles = files.filter(file => file?.status === 'passed').length;
  if (passedFiles === 0) return null;
  return `Test Files  ${passedFiles} passed (${files.length})\n`
    + `Tests  ${report.numPassedTests} passed (${report.numTotalTests})`;
}

export async function runUnitTests(args, run = runAngular, writeLine = console.log) {
  const reportDir = await mkdtemp(join(tmpdir(), 'agent-studio-vitest-'));
  const reportPath = join(reportDir, 'report.json');
  try {
    const code = await run(args, reportPath);
    if (code !== 0) return code;
    let report;
    try {
      report = JSON.parse(await readFile(reportPath, 'utf8'));
    } catch (error) {
      console.error(`Vitest completed without a readable report at ${reportPath}: ${error.message}`);
      return 2;
    }
    const summary = completedTestSummary(report);
    if (!summary) {
      console.error(`Vitest completed without test results in ${reportPath}`);
      return 2;
    }
    writeLine(summary);
    return 0;
  } finally {
    await rm(reportDir, { recursive: true, force: true });
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  process.exitCode = await runUnitTests(process.argv.slice(2));
}
