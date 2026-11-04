import type { StatusTone } from '../../../components/status-indicator/status-indicator.component';

/** Server readiness report (AGT-3412): `GET /api/system/readiness`. */
export type ReadinessState = 'pass' | 'unknown' | 'warn' | 'fail';
export type ReadinessScope = 'server' | 'project' | 'host';

export interface ReadinessCheck {
  id: string;
  title: string;
  scope: ReadinessScope;
  subject: string | null;
  state: ReadinessState;
  meaning: string;
  remedy: string | null;
}

export interface ServerReadinessReport {
  /** Null until the server finished its first collection. */
  computedAt: string | null;
  state: ReadinessState | 'pending';
  failing: number;
  attention: number;
  checks: ReadinessCheck[];
}

export const READINESS_STATE_LABEL: Record<ReadinessState, string> = {
  pass: 'Passing',
  unknown: 'Unknown',
  warn: 'Attention',
  fail: 'Failing',
};

export const READINESS_STATE_TONE: Record<ReadinessState, StatusTone> = {
  pass: 'success',
  unknown: 'neutral',
  warn: 'warning',
  fail: 'danger',
};

export interface ReadinessGroup {
  key: string;
  label: string;
  checks: ReadinessCheck[];
}

/** Server lines first, then execution hosts, then one group per project, in report order. */
export function groupReadiness(checks: readonly ReadinessCheck[]): ReadinessGroup[] {
  const groups = new Map<string, ReadinessGroup>();
  const add = (key: string, label: string, check: ReadinessCheck) => {
    const group = groups.get(key) ?? { key, label, checks: [] };
    group.checks.push(check);
    groups.set(key, group);
  };
  for (const check of checks) {
    if (check.scope === 'project') add(`project:${check.subject ?? ''}`, `Project ${check.subject ?? ''}`, check);
    else if (check.scope === 'host') add('host', 'Execution hosts', check);
    else add('server', 'Server', check);
  }
  const order = (group: ReadinessGroup) => group.key === 'server' ? 0 : group.key === 'host' ? 1 : 2;
  return [...groups.values()].sort((a, b) => order(a) - order(b));
}

/** Stable row identity: the check id plus what it is about. */
export function readinessKey(check: ReadinessCheck): string {
  return `${check.scope}:${check.id}:${check.subject ?? ''}`;
}
