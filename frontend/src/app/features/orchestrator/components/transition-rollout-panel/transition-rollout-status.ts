import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** One state shown through the shared status indicator. */
export interface RolloutStatus {
  tone: StatusTone;
  label: string;
}

export type ProfileState = 'monolith' | 'shadow' | 'ready' | 'canary' | 'engine' | 'rolled-back';

const PROFILE_STATE_TONES: Readonly<Record<ProfileState, StatusTone>> = {
  monolith: 'neutral',
  shadow: 'info',
  ready: 'success',
  canary: 'running',
  engine: 'success',
  'rolled-back': 'warning',
};

/** Transition profile state to tone; a state the backend adds later stays neutral. */
export function profileStateTone(state: string): StatusTone {
  return PROFILE_STATE_TONES[state as ProfileState] ?? 'neutral';
}

const CONTRACT_TONES: Readonly<Record<string, StatusTone>> = {
  covered: 'success',
  uncovered: 'warning',
};

/** Applier contract coverage; `null` means the backend did not report one. */
export function applierContractStatus(status: string | null | undefined): RolloutStatus {
  if (!status) return { tone: 'neutral', label: 'not reported' };
  return { tone: CONTRACT_TONES[status] ?? 'neutral', label: status };
}

const CANARY_TONES: Readonly<Record<string, StatusTone>> = {
  watching: 'running',
  passed: 'success',
  failed: 'danger',
  stopped: 'neutral',
};

/** Applier canary status; `null` means no canary has run. */
export function canaryStatus(status: string | null | undefined): RolloutStatus {
  if (!status) return { tone: 'neutral', label: 'not run' };
  return { tone: CANARY_TONES[status] ?? 'neutral', label: status };
}

/** AGT-3218: what the effect probe found for one applied engine decision. */
export interface EffectCheck {
  occurrence: string;
  action: string;
  verified: boolean;
  missing: string[];
  evidence: string;
  checkedAt: string;
  basis?: 'row' | 'ask' | 'checkpoint' | 'review-input-handoff' | 'agent-round-handoff'
    | 'source-exit' | 'gate-superseded' | 'probe-failed';
  completedAt?: string | null;
  awaitingDownstream?: boolean;
  waitingOn?: 'card-not-found' | 'integration-in-flight' | null;
}

/** Why a check is not counted although it is no applier defect. */
const REFUSED: Partial<Record<NonNullable<EffectCheck['basis']>, string>> = {
  'source-exit': 'not counted, card left the source lane',
  'gate-superseded': 'not counted, card moved on before the gate dispatch',
  'probe-failed': 'not counted, probe failed',
};

const WAITS: Record<NonNullable<EffectCheck['waitingOn']>, string> = {
  'card-not-found': 'waiting for the card',
  'integration-in-flight': 'integration in flight',
};

/** Result of the last effect check; `null` means none has run yet. */
export function effectCheckStatus(check: EffectCheck | null | undefined): RolloutStatus {
  if (!check) return { tone: 'neutral', label: 'none yet' };
  const refused = check.basis ? REFUSED[check.basis] : undefined;
  if (refused) return { tone: 'neutral', label: refused };
  if (check.verified) {
    return check.basis && check.basis !== 'row'
      ? { tone: 'neutral', label: `not counted, ${check.basis}` }
      : { tone: 'success', label: 'applied' };
  }
  if (check.waitingOn) return { tone: 'info', label: WAITS[check.waitingOn] };
  if (!check.completedAt && check.missing.includes('DecisionReceipt')) {
    return { tone: 'running', label: 'applier still running' };
  }
  return check.awaitingDownstream
    ? { tone: 'info', label: `waiting for ${check.missing.join(', ')}` }
    : { tone: 'danger', label: `missing ${check.missing.join(', ')}` };
}

/** Shadow decision against the deterministic baseline. */
export function decisionMatchStatus(matchesDeterministic: boolean, callStatus: string): RolloutStatus {
  if (matchesDeterministic) return { tone: 'success', label: 'match' };
  if (callStatus === 'completed') return { tone: 'warning', label: 'differs' };
  return { tone: callStatus === 'failed' ? 'danger' : 'neutral', label: callStatus };
}
