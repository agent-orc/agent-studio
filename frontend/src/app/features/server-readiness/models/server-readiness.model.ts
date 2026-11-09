import type { RemedyAction } from '../../../models/remedy-action.model';

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
  /** The fix, as prose. */
  remedy: string | null;
  /** AGT-3580: the shell command behind the remedy, shown only as a copyable detail. */
  command?: string | null;
  /** AGT-3580: the owning setting or surface, rendered as a link. */
  action?: RemedyAction | null;
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
