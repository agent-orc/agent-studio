import { describe, expect, it } from 'vitest';
import {
  applierContractStatus,
  canaryStatus,
  decisionMatchStatus,
  effectCheckStatus,
  profileStateTone,
  type EffectCheck,
} from './transition-rollout-status';

const check = (overrides: Partial<EffectCheck>): EffectCheck => ({
  occurrence: 'orch_1', action: 'CreateReview', verified: false, missing: [],
  evidence: '', checkedAt: '2026-10-19T08:00:00Z', ...overrides,
});

describe('transition rollout status mapping', () => {
  it('maps every profile state onto a tone and falls back to neutral', () => {
    expect(profileStateTone('monolith')).toBe('neutral');
    expect(profileStateTone('shadow')).toBe('info');
    expect(profileStateTone('ready')).toBe('success');
    expect(profileStateTone('canary')).toBe('running');
    expect(profileStateTone('engine')).toBe('success');
    expect(profileStateTone('rolled-back')).toBe('warning');
    expect(profileStateTone('later-state')).toBe('neutral');
  });

  it('maps applier contract coverage', () => {
    expect(applierContractStatus('covered')).toEqual({ tone: 'success', label: 'covered' });
    expect(applierContractStatus('uncovered')).toEqual({ tone: 'warning', label: 'uncovered' });
    expect(applierContractStatus(null)).toEqual({ tone: 'neutral', label: 'not reported' });
  });

  it('maps applier canary status', () => {
    expect(canaryStatus('watching').tone).toBe('running');
    expect(canaryStatus('passed').tone).toBe('success');
    expect(canaryStatus('failed').tone).toBe('danger');
    expect(canaryStatus('stopped').tone).toBe('neutral');
    expect(canaryStatus(undefined)).toEqual({ tone: 'neutral', label: 'not run' });
  });

  it('maps the last effect check result', () => {
    expect(effectCheckStatus(null)).toEqual({ tone: 'neutral', label: 'none yet' });
    expect(effectCheckStatus(check({ verified: true, basis: 'row' }))).toEqual({ tone: 'success', label: 'applied' });
    expect(effectCheckStatus(check({ verified: true, basis: 'ask' }))).toEqual({ tone: 'neutral', label: 'not counted, ask' });
    expect(effectCheckStatus(check({ basis: 'gate-superseded' })))
      .toEqual({ tone: 'neutral', label: 'not counted, card moved on before the gate dispatch' });
    expect(effectCheckStatus(check({ waitingOn: 'card-not-found' }))).toEqual({ tone: 'info', label: 'waiting for the card' });
    expect(effectCheckStatus(check({ waitingOn: 'integration-in-flight' }))).toEqual({ tone: 'info', label: 'integration in flight' });
    expect(effectCheckStatus(check({ missing: ['DecisionReceipt'], completedAt: null })))
      .toEqual({ tone: 'running', label: 'applier still running' });
    expect(effectCheckStatus(check({ missing: ['BatchAdmission'] }))).toEqual({ tone: 'danger', label: 'missing BatchAdmission' });
    expect(effectCheckStatus(check({ missing: ['GateDispatch'], awaitingDownstream: true })))
      .toEqual({ tone: 'info', label: 'waiting for GateDispatch' });
  });

  it('maps the shadow decision match', () => {
    expect(decisionMatchStatus(true, 'completed')).toEqual({ tone: 'success', label: 'match' });
    expect(decisionMatchStatus(false, 'completed')).toEqual({ tone: 'warning', label: 'differs' });
    expect(decisionMatchStatus(false, 'failed')).toEqual({ tone: 'danger', label: 'failed' });
    expect(decisionMatchStatus(false, 'unavailable')).toEqual({ tone: 'neutral', label: 'unavailable' });
  });
});
