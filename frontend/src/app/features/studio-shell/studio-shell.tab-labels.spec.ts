import { describe, expect, it } from 'vitest';
import { titleFromDocumentPath, truncateTabTitle } from './studio-shell.tab-labels';

describe('studio tab labels', () => {
  it('truncates long labels at a word boundary', () => {
    expect(truncateTabTitle('Stale branch sweep detects abandoned remote branches', 32))
      .toBe('Stale branch sweep detects…');
  });

  it('turns a document slug into a readable fallback title', () => {
    expect(titleFromDocumentPath('operations/pre-develop-gate-a-run-budget-overrun.md'))
      .toBe('Pre Develop Gate A Run Budget Overrun');
  });

  it('keeps a short title unchanged', () => {
    expect(truncateTabTitle('Board')).toBe('Board');
  });
});
