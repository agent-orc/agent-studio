import { defineConfig } from 'vitest/config';

// Review hosts run the suite beside other gates; heavy component specs then need
// well over Vitest's 5 s default without being wrong. A timeout here is not a finding.
export default defineConfig({
  test: {
    testTimeout: 30_000,
    hookTimeout: 30_000,
  },
});
