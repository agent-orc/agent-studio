import { describe, expect, it } from 'vitest';
import { displaySignInCode, normalizeSignInCode, shortThumbprint, terminalSignInFromHash } from './terminal-sign-in.model';

describe('terminal sign-in model', () => {
  it('reads the confirmation route and its code from the hash', () => {
    expect(terminalSignInFromHash('#/terminal-sign-in?code=WDJBMJHT')).toEqual({ code: 'WDJBMJHT' });
    expect(terminalSignInFromHash('#/terminal-sign-in?code=wdjb-mjht')).toEqual({ code: 'WDJBMJHT' });
    expect(terminalSignInFromHash('#/terminal-sign-in')).toEqual({ code: null });
    expect(terminalSignInFromHash('#/terminal-sign-in?code=AEIOUAEI')).toEqual({ code: null });
    expect(terminalSignInFromHash('#/board')).toBeNull();
    expect(terminalSignInFromHash('')).toBeNull();
  });

  it('normalizes and displays codes of the eight-character consonant alphabet', () => {
    expect(normalizeSignInCode(' wdjb mjht ')).toBe('WDJBMJHT');
    expect(normalizeSignInCode('WDJB-MJH')).toBeNull();
    expect(normalizeSignInCode('WDJB-MJH1')).toBeNull();
    expect(displaySignInCode('WDJBMJHT')).toBe('WDJB-MJHT');
    expect(shortThumbprint('NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs')).toBe('NzbLsXh8uDCcd-6M…');
  });
});
