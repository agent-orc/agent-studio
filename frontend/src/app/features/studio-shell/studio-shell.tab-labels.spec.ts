import { describe, expect, it } from 'vitest';
import { tabAccessibleName, titleFromDocumentPath, truncateTabTitle } from './studio-shell.tab-labels';

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

describe('tabAccessibleName (AGT-3835)', () => {
  it('starts with the prefix chip and title a task tab shows', () => {
    expect(tabAccessibleName('DEMO-5', 'Add CSV export to reports', 'Add CSV export to reports'))
      .toBe('DEMO-5 Add CSV export to reports');
  });

  it('keeps a full label that already reads like the visible text', () => {
    expect(tabAccessibleName('DEMO', 'Deck', 'DEMO · Deck')).toBe('DEMO · Deck');
  });

  it('appends the full label when the strip shows a shorter form', () => {
    expect(tabAccessibleName('All', 'Board', 'All projects · Board')).toBe('All Board, All projects · Board');
  });
});
