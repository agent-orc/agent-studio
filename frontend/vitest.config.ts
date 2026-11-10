import { defineConfig } from 'vitest/config';

const consoleReporter = process.env['AGENT_STUDIO_VITEST_REPORTER'] === 'verbose' ? 'verbose' : 'default';

// Review hosts run the suite beside other gates; heavy component specs then need
// well over Vitest's 5 s default without being wrong. A timeout here is not a finding.
export default defineConfig({
  test: {
    // The CI wrapper gives every run its own file. Keep the console reporter for
    // local feedback and write a result that can be checked after Angular exits.
    // A review clean repeat asks for the verbose reporter, which names every
    // passing test, so it can prove the previously failed test ran (AGT-3650).
    reporters: process.env['AGENT_STUDIO_VITEST_REPORT']
      ? [consoleReporter, ['json', { outputFile: process.env['AGENT_STUDIO_VITEST_REPORT'] }]]
      : [consoleReporter],
    testTimeout: 30_000,
    hookTimeout: 30_000,
    pool: 'threads',
    maxWorkers: 4,
    fileParallelism: true,
    isolate: false,
    coverage: {
      provider: 'v8',
      reportsDirectory: '../coverage/frontend',
      reporter: [['lcov', { projectRoot: '..' }]],
    },
  },
});
