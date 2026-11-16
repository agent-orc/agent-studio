import { nameWithVisibleText } from './accessible-name';

describe('nameWithVisibleText', () => {
  it('starts the name with the visible text and keeps the detail after it', () => {
    expect(nameWithVisibleText('Needs you 3', '3 items need you')).toBe('Needs you 3, 3 items need you');
  });

  it('keeps a detail that already starts with the visible words', () => {
    expect(nameWithVisibleText('DEMO Board', 'DEMO · Board')).toBe('DEMO · Board');
  });

  it('drops a detail the visible text already contains', () => {
    expect(nameWithVisibleText('DEMO-5 Add CSV export…', 'Add CSV export')).toBe('DEMO-5 Add CSV export…');
  });

  it('collapses whitespace and falls back to the detail without visible text', () => {
    expect(nameWithVisibleText('  Recovery\n  pending 2 ', null)).toBe('Recovery pending 2');
    expect(nameWithVisibleText('', 'Close tab')).toBe('Close tab');
  });
});
