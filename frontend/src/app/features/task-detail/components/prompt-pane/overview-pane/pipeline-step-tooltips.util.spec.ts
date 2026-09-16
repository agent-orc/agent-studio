import { describe, expect, it } from 'vitest';
import {
  buildConcernTooltip,
  buildDecisionTooltip,
  buildStepStatusTooltip,
  decisionTooltipSeverity,
  reconcileCoreVerdict,
  verdictTitle,
} from './pipeline-step-tooltips.util';
import type { SteeringInfo } from '../../../../../components/steering-detail';

/**
 * AGT-2819 split these pure builders out of `overview-pane.component.ts`. They
 * were only reachable through a rendered pane before; here they are exercised
 * directly.
 */
describe('verdictTitle', () => {
  it('maps every recognised verdict token to its title-case label', () => {
    expect(verdictTitle('concern')).toBe('Concerns');
    expect(verdictTitle('CONCERNS')).toBe('Concerns');
    expect(verdictTitle(null)).toBeNull();
    expect(verdictTitle('')).toBeNull();
  });
});

describe('reconcileCoreVerdict', () => {
  it('keeps the recorded verdict on a passed step', () => {
    expect(reconcileCoreVerdict('passed', 'success')).toBe('success');
  });

  it('drops a success claim a non-passed step cannot honestly make', () => {
    expect(reconcileCoreVerdict('failed', 'success')).toBeNull();
    expect(reconcileCoreVerdict('failed', 'noop')).toBeNull();
  });

  it('keeps any other verdict on a non-passed step', () => {
    expect(reconcileCoreVerdict('failed', 'escalate')).toBe('escalate');
  });
});

describe('buildConcernTooltip', () => {
  it('is null unless there is both a summary and a recognised verdict', () => {
    expect(buildConcernTooltip('Step', 'concerns', '  ')).toBeNull();
    expect(buildConcernTooltip('Step', null, 'Something to note')).toBeNull();
  });

  it('titles the tooltip with the step label and the verdict kind', () => {
    const tip = buildConcernTooltip('Code quality', 'concerns', 'Naming drifts.');
    expect(tip?.title).toBe('Code quality · Concerns');
    expect(tip?.body).toBe('Naming drifts.');
  });
});

describe('buildStepStatusTooltip', () => {
  it('is null for a status that carries no failure or coverage detail', () => {
    expect(buildStepStatusTooltip('Step', 'running', 'busy')).toBeNull();
    expect(buildStepStatusTooltip('Step', 'failed', '   ')).toBeNull();
  });

  it('explains a failure', () => {
    const tip = buildStepStatusTooltip('Build', 'failed', 'exit 1');
    expect(tip?.title).toContain('Build');
    expect(tip?.body).toBe('exit 1');
  });

  it('keeps the honest coverage scope of a passed staged test gate', () => {
    const tip = buildStepStatusTooltip('Tests', 'passed', 'test-level=routine; selected=4');
    expect(tip).not.toBeNull();
    expect(tip?.body).toContain('test-level=routine');
  });
});

describe('decisionTooltipSeverity and buildDecisionTooltip', () => {
  function info(overrides: Partial<SteeringInfo> = {}): SteeringInfo {
    return {
      verdict: 'escalate',
      verdictLabel: 'Escalate',
      tone: 'warn',
      reason: 'Acceptance item 3 is unproven.',
      openItems: [{ aspect: 'tests-and-evidence', verdict: 'block', reason: 'No regression test' }],
      prompt: null,
      context: [{ key: 'Re-issues', value: '2' }],
      commits: [],
      ...overrides,
    } as SteeringInfo;
  }

  it('maps every steering tone to its tooltip severity', () => {
    expect(decisionTooltipSeverity('warn')).toBe('warn');
    expect(decisionTooltipSeverity('ok')).toBe('success');
    expect(decisionTooltipSeverity('danger')).toBe('error');
  });

  it('titles the decision and keeps its reason and open items in the body', () => {
    const tip = buildDecisionTooltip(info());
    expect(tip.title).toBe('Decision · Escalate');
    expect(tip.body).toContain('Acceptance item 3 is unproven.');
    expect(tip.body).toContain('tests-and-evidence');
    expect(tip.body).toContain('No regression test');
    expect(tip.body).toContain('Re-issues: 2');
  });

  it('falls back to the verdict label when there is nothing else to say', () => {
    const tip = buildDecisionTooltip(info({ reason: '', openItems: [], context: [] }));
    expect(tip.body).toBe('Escalate');
  });
});
