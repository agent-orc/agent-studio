/** `0` -> `0s`; `83410` -> `1m23s`; `3624000` -> `1h0m`. */
export function formatLaneElapsed(ms: number): string {
  const totalSec = Math.floor(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const m = Math.floor(totalSec / 60);
  const s = totalSec % 60;
  if (m < 60) return `${m}m${s.toString().padStart(2, '0')}s`;
  const h = Math.floor(m / 60);
  const mm = m % 60;
  return `${h}h${mm.toString().padStart(2, '0')}m`;
}
