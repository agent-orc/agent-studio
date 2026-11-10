import { defineConfig } from 'vitest/config';

// Review hosts run the suite beside other gates; heavy component specs then need
// well over Vitest's 5 s default without being wrong. A timeout here is not a finding.
export default defineConfig({
  test: {
    // The CI wrapper gives every run its own file. Keep the console reporter for
    // local feedback and write a result that can be checked after Angular exits.
    reporters: process.env['AGENT_STUDIO_VITEST_REPORT']
      ? ['default', ['json', { outputFile: process.env['AGENT_STUDIO_VITEST_REPORT'] }]]
      : ['default'],
    testTimeout: 30_000,
    hookTimeout: 30_000,
    pool: 'threads',
    maxWorkers: 4,
    fileParallelism: true,
    isolate: false,
    // Angular's ng test command does not forward Vitest's --sequence.shuffle
    // option. These settings provide the same seeded ordering for gate checks.
    sequence: process.env['FRONTEND_TEST_SHUFFLE'] === '1'
      ? { shuffle: true, seed: Number(process.env['FRONTEND_TEST_SEED'] ?? Date.now()) }
      : undefined,
    coverage: {
      provider: 'v8',
      reportsDirectory: '../coverage/frontend',
      reporter: [['lcov', { projectRoot: '..' }]],
    },
  },
});
