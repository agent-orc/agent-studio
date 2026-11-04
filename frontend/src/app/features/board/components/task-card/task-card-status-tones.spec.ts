import { describe, expect, it } from 'vitest';
import {
  commitEmptyTone,
  decisionTone,
  dependencyTone,
  executionTone,
  gradeTone,
  issueTone,
  loopTone,
  mergeTone,
  phaseTone,
  reviewTone,
} from './task-card-status-tones';

describe('task card status tones (AGT-3403)', () => {
  it('maps every card badge state onto the shared tone', () => {
    expect([gradeTone('a'), gradeTone('b'), gradeTone('c'), gradeTone('d')])
      .toEqual(['success', 'info', 'warning', 'danger']);
    expect([dependencyTone('open'), dependencyTone('ready'), dependencyTone('cycle'), dependencyTone('blocked')])
      .toEqual(['warning', 'success', 'danger', 'danger']);
    expect([
      phaseTone('steer-pending'), phaseTone('loop-waiting'), phaseTone('intake-running'), phaseTone('intake-blocked'),
      phaseTone('intake-passed'), phaseTone('post-processing-running'), phaseTone('post-processing-blocked'),
      phaseTone('awaiting-review'), phaseTone('integrating'), phaseTone('human-ready'),
    ]).toEqual(['warning', 'warning', 'running', 'danger', 'success', 'running', 'danger', 'info', 'running', 'info']);
    expect([
      executionTone('running'), executionTone('failed'), executionTone('noop'),
      executionTone('blocked'), executionTone('needs-input'), executionTone('stopped'),
    ]).toEqual(['running', 'danger', 'neutral', 'warning', 'warning', 'neutral']);
    expect([reviewTone('generating'), reviewTone('ready'), reviewTone('failed'), reviewTone('degraded')])
      .toEqual(['running', 'success', 'danger', 'warning']);
    expect([issueTone('info'), issueTone('warn'), issueTone('high')]).toEqual(['info', 'warning', 'danger']);
    expect([commitEmptyTone('no-code'), commitEmptyTone('discovery'), commitEmptyTone('integrated')])
      .toEqual(['neutral', 'warning', 'success']);
    expect([decisionTone(true), decisionTone(false)]).toEqual(['warning', 'neutral']);
    expect([loopTone(false), loopTone(true)]).toEqual(['info', 'warning']);
    expect([mergeTone(true), mergeTone(false)]).toEqual(['success', 'neutral']);
  });

  it('falls back to neutral for a state the backend adds later', () => {
    for (const fn of [gradeTone, dependencyTone, phaseTone, executionTone, reviewTone, issueTone, commitEmptyTone]) {
      expect(fn('something-new')).toBe('neutral');
      expect(fn(null)).toBe('neutral');
      expect(fn('toString')).toBe('neutral');
    }
  });
});
