import type { StatusTone } from '../components/status-indicator/status-indicator.component';

/** Reachability of a run host as `GET /api/deploy/run-hosts` reports it (F4). */
export type RunHostState = 'reachable' | 'unreachable' | 'no-contact' | 'not-enrolled';

/** One run host: an enrolled identity whose credential holds only runner.deploy. */
export interface RunHostStatus {
  id: string;
  name: string;
  state: RunHostState;
  projects: readonly string[];
  lastSeenAt: string | null;
  lastClaimAt: string | null;
  lastReportAt: string | null;
}

/** The words for a run host state where an environment names its host. */
export function runHostStateLabel(state: RunHostState): string {
  switch (state) {
    case 'reachable': return 'Reachable';
    case 'unreachable': return 'Unreachable';
    case 'no-contact': return 'No contact yet';
    case 'not-enrolled': return 'Not enrolled';
  }
}

/** Only a host that stopped answering warns; one that never answered is a setup step, not an alarm (R4). */
export function runHostStateTone(state: RunHostState): StatusTone {
  return state === 'reachable' ? 'success' : state === 'unreachable' ? 'warning' : 'neutral';
}
