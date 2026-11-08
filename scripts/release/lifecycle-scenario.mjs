#!/usr/bin/env node
// Application-lifecycle scenario (AGT-3178): a simulated user takes "Click
// Counter" from a template to production inside a weekly quota, on a real
// installation. See docs/operations/testing/lifecycle-scenario.md.
import { playScenario } from './lifecycle-scenario/play.mjs';
import { USAGE, UsageError, parseConfig } from './lifecycle-scenario/config.mjs';

let config;
try {
  config = parseConfig(process.argv.slice(2), process.env);
} catch (error) {
  process.stderr.write(`${error.message}\n\n${error instanceof UsageError ? USAGE : ''}`);
  process.exit(2);
}
if (config.help) {
  process.stdout.write(USAGE);
  process.exit(0);
}
const report = await playScenario(config, { log: line => process.stdout.write(`${line}\n`) });
process.stdout.write(`lifecycle: ${report.status}; evidence: ${config.reportDir}\n`);
process.exit(report.status === 'passed' ? 0 : 1);
