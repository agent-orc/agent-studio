import { describe, expect, it } from 'vitest';
import { pipelineStepPurpose } from './pipeline-config.util';

describe('pipelineStepPurpose', () => {
  it('returns the catalogue description for a known step id', () => {
    expect(pipelineStepPurpose('post-summary-assessment')).toContain('Closing Result assessment');
    expect(pipelineStepPurpose('core-agent-run')).toContain('task-owning CLI agent');
  });

  it('falls back to the family description for aspect and drift steps', () => {
    expect(pipelineStepPurpose('aspect-security')).toContain('LLM aspect review');
    expect(pipelineStepPurpose('post-drift-docs')).toContain('drift analysis');
  });

  it('returns the generic description for an unknown step id', () => {
    expect(pipelineStepPurpose('post-unknown-step')).toBe('Pipeline step in the project processing flow.');
  });
});
