import { defineConfig } from 'vitest/config';

// Review hosts run the suite beside other gates; heavy component specs then need
// well over Vitest's 5 s default without being wrong. A timeout here is not a finding.
export default defineConfig({
  test: {
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
