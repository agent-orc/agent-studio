import { describe, expect, it } from 'vitest';
import {
  FINAL_VERDICT_STEP_ID,
  PIPELINE_PHASES,
  buildStepExplanation,
  pipelinePhaseForKind,
} from './pipeline-step-explanations.util';
import type { StepKind } from '../../../../task-pipeline';

/**
 * AGT-2819 split the pipeline phase catalogue and the per-step explanation copy
 * out of `overview-pane.component.ts`. These cases pin the two invariants the
 * pane relied on: every kind resolves to a phase, and every step explains
 * itself.
 */
describe('pipelinePhaseForKind', () => {
  const kinds: StepKind[] = ['module', 'core', 'aspect', 'orchestrator', 'tool', 'analysis', 'drift'];

  it('resolves every catalogue step kind to a known phase', () => {
    for (const kind of kinds) {
      const phase = pipelinePhaseForKind(kind);
      expect(PIPELINE_PHASES[phase.key]).toBe(phase);
      expect(phase.label.length).toBeGreaterThan(0);
      expect(phase.description.length).toBeGreaterThan(0);
    }
  });

  it('every phase row is keyed by itself, so a lookup can never drift', () => {
    for (const [key, phase] of Object.entries(PIPELINE_PHASES)) {
      expect(phase.key).toBe(key);
    }
  });
});

describe('buildStepExplanation', () => {
  it('prefers the per-step copy for a known catalogue id', () => {
    const tip = buildStepExplanation('pre-loop-guard', 'Loop guard', 'tool');
    expect(tip.title).toBe('Loop guard');
    expect(tip.body).toContain('loop guard');
  });

  it('is case-insensitive on the step id', () => {
    expect(buildStepExplanation('PRE-LOOP-GUARD', 'Loop guard', 'tool').body)
      .toBe(buildStepExplanation('pre-loop-guard', 'Loop guard', 'tool').body);
  });

  it('falls back to the per-kind copy for an unknown step, never to a bare tooltip', () => {
    const tip = buildStepExplanation('some-future-step', 'Future step', 'tool');
    expect(tip.title).toBe('Future step');
    expect(tip.body).toContain('tooling step');
  });

  it('names the single final orchestrator ruling', () => {
    expect(FINAL_VERDICT_STEP_ID).toBe('post-orchestrator-decision');
  });
});
