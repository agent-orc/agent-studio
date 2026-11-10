// Timing assertions should measure the test worker's work, not time spent
// waiting for CPU on a shared runner. Browsers use the wall clock fallback.
const threadCpuUsage = (globalThis as {
  process?: { threadCpuUsage?: () => { user: number; system: number } };
}).process?.threadCpuUsage;

export const structureTestClockMs = threadCpuUsage
  ? () => {
    const { user, system } = threadCpuUsage();
    return (user + system) / 1000;
  }
  : () => performance.now();
