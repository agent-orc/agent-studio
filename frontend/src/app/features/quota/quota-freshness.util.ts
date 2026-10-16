import type { QuotaSnapshot } from './models/quota.model';

export function formatQuotaAge(ms: number): string {
  if (!Number.isFinite(ms)) return 'never';
  const sec = Math.floor(ms / 1000);
  if (sec < 5) return 'just now';
  if (sec < 60) return `${sec} s ago`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min} min ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr} h ago`;
  const d = Math.floor(hr / 24);
  return `${d} d ago`;
}

export function quotaSnapshotIsStale(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  now: number,
): boolean {
  if (snapshot.probeFailedAt) return true;
  const fetchedMs = snapshot.fetchedAt ? Date.parse(snapshot.fetchedAt) : NaN;
  return !Number.isFinite(fetchedMs) || Math.max(0, now - fetchedMs) > ttlMs;
}

export function quotaProbeFailureLabel(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.probeFailedAt) return null;
  const failedMs = Date.parse(snapshot.probeFailedAt);
  const time = Number.isFinite(failedMs)
    ? new Date(failedMs).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
  const cli = snapshot.cliType.toLowerCase();
  const version = normalizedVersion(snapshot.cliVersion);
  return `probe failed ${time}, ${cli}${version ? ` ${version}` : ''}`;
}

function normalizedVersion(raw: string | null | undefined): string | null {
  if (!raw?.trim()) return null;
  return raw.trim()
    .replace(/^codex-cli\s+/i, '')
    .replace(/^claude(?:\s+code)?\s+/i, '');
}
