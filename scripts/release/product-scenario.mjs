#!/usr/bin/env node
// Product scenario (AGT-3183): a product manager, a designer and a manager
// evolve "Click Counter" through Studio on a real installation, after the
// lifecycle scenario (AGT-3178). See docs/operations/testing/product-scenario.md.
import { playScenario } from './product-scenario/play.mjs';
import { USAGE, UsageError, parseConfig } from './product-scenario/config.mjs';

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
process.stdout.write(`product: ${report.status}; evidence: ${config.reportDir}\n`);
process.exit(report.status === 'passed' ? 0 : 1);
