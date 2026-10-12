import { mergeConfig, defineConfig } from 'vitest/config';
import review from './vitest.config';

// Release coverage keeps Vitest's default file isolation.
export default mergeConfig(review, defineConfig({ test: { isolate: true } }));
