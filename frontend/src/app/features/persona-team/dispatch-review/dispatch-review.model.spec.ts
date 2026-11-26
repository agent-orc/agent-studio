import { describe, expect, it } from 'vitest';
import { defaultWeights, dispatchRoute, validWeights } from './dispatch-review.model';
describe('dispatch weight editor', () => {
  it('accepts defaults and both gate boundaries', () => {
    expect(validWeights(defaultWeights(), .8)).toBe(true);
    expect(validWeights(defaultWeights(), .5)).toBe(true);
    expect(validWeights(defaultWeights(), 1)).toBe(true);
  });
  it('rejects a balanced negative or fractional weight and an invalid gate', () => {
    expect(validWeights({ ...defaultWeights(), conflicts: -1, tokens: 56 }, .8)).toBe(false);
    expect(validWeights({ ...defaultWeights(), conflicts: 30.5, tokens: 24.5 }, .8)).toBe(false);
    for (const gate of [NaN, Infinity, .49, 1.01]) expect(validWeights(defaultWeights(), gate)).toBe(false);
    expect(validWeights({ ...defaultWeights(), conflicts: NaN }, .8)).toBe(false);
  });
  it('rejects a remainder and returns an independent default editor for Reset', () => {
    const edited = defaultWeights(); edited.conflicts = 31;
    expect(validWeights(edited, .8)).toBe(false);
    expect(defaultWeights().conflicts).toBe(30);
  });
  it('keeps project identities in the existing Team route segment', () => {
    expect(dispatchRoute('PROJ-002')).toBe('dispatch:PROJ-002');
  });
});
