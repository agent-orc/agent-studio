import { describe, expect, it } from 'vitest';
import type { TaskArtifact } from '../../../../../models/task.model';
import { compareDocuments, documentVerdictStatusTone, presentDocument } from './document-presentation.util';

function artifact(overrides: Partial<TaskArtifact>): TaskArtifact {
  return {
    name: 'report.md',
    sizeBytes: 100,
    mtime: '2026-07-11T12:00:00Z',
    kind: 'other',
    ...overrides,
  };
}

describe('document presentation', () => {
  it('strips code-review frontmatter and promotes topic, verdict, and model', () => {
    const file = artifact({ name: 'code-review-grade.md', kind: 'codeReview' });
    const view = presentDocument(file, `---\nverdict: concerns\ngrade: C\nmodel: gpt-5\n---\n\n# Review findings\n\nKeep this.`, null);

    expect(view.title).toBe('Review findings');
    expect(view.verdict).toBe('Concerns');
    expect(view.verdictTone).toBe('concerns');
    expect(view.model).toBe('gpt-5');
    expect(view.body).toContain('Keep this.');
    expect(view.body).not.toContain('verdict: concerns');
  });

  it('puts results before source prompts and raw artifacts', () => {
    const files = [
      artifact({ name: 'prompt.md', kind: 'prompt' }),
      artifact({ name: 'raw.html', kind: 'other' }),
      artifact({ name: 'aspect-tests.json', kind: 'aspect', aspectName: 'tests' }),
      artifact({ name: 'code-review-grade.md', kind: 'codeReview' }),
    ].sort(compareDocuments);
    expect(files.map((file) => file.kind)).toEqual(['codeReview', 'aspect', 'prompt', 'other']);
  });
});

describe('documentVerdictStatusTone', () => {
  it('maps document verdict tones onto status tones', () => {
    expect(documentVerdictStatusTone('pass')).toBe('success');
    expect(documentVerdictStatusTone('concerns')).toBe('warning');
    expect(documentVerdictStatusTone('block')).toBe('danger');
    expect(documentVerdictStatusTone('neutral')).toBe('neutral');
    expect(documentVerdictStatusTone(null)).toBe('neutral');
  });
});
