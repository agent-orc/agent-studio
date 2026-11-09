import { anomalyLabel, areaLabel, daysUntil, severityPresentation, statePresentation, userStatePresentation } from './operations-security.model';

describe('operations security presentation', () => {
  it('maps every severity to one tone, acute only for critical and high', () => {
    expect(['critical', 'high', 'medium', 'low', 'info'].map(severity => severityPresentation(severity)))
      .toEqual([{ tone: 'danger', label: 'Critical' }, { tone: 'danger', label: 'High' }, { tone: 'warning', label: 'Medium' },
        { tone: 'info', label: 'Low' }, { tone: 'neutral', label: 'Info' }]);
    expect(severityPresentation('later')).toEqual({ tone: 'neutral', label: 'later' });
  });

  it('maps credential, host and audit states and falls back to neutral', () => {
    const tones = Object.fromEntries(['active', 'expiring', 'no-expiry', 'missing', 'unexpected', 'expired', 'revoked',
      'current', 'behind', 'not-reported', 'verified', 'failed', 'brand-new'].map(state => [state, statePresentation(state).tone]));
    expect(tones).toEqual({
      active: 'success', expiring: 'warning', 'no-expiry': 'warning', missing: 'danger', unexpected: 'danger', expired: 'neutral',
      revoked: 'neutral', current: 'success', behind: 'warning', 'not-reported': 'neutral', verified: 'success', failed: 'danger',
      'brand-new': 'neutral',
    });
    expect(userStatePresentation('missing')).toEqual({ tone: 'danger', label: 'No second factor' });
    expect(userStatePresentation('covered').tone).toBe('success');
  });

  it('labels areas and anomaly kinds in sentence case', () => {
    expect(areaLabel('second-factor')).toBe('Second factor');
    expect(areaLabel('cards')).toBe('Security cards');
    expect(anomalyLabel('refused-secret-write')).toBe('Refused secret write');
    expect(anomalyLabel('other')).toBe('other');
  });

  it('counts whole days until a date, rounded up', () => {
    const now = Date.parse('2026-11-09T12:00:00Z');
    expect(daysUntil('2026-12-03T00:00:00Z', now)).toBe(24);
    expect(daysUntil('2026-11-09T18:00:00Z', now)).toBe(1);
    expect(daysUntil('2026-11-08T12:00:00Z', now)).toBe(-1);
    expect(daysUntil(null, now)).toBeNull();
    expect(daysUntil('not a date', now)).toBeNull();
  });
});
