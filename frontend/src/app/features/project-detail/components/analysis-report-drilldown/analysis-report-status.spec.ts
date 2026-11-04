import { describe, expect, it } from 'vitest';
import { findingSeverityTone, parseStatusTone, reportSeverityTone } from './analysis-report-status';

describe('analysis report status tones', () => {
  it('maps report severity, with unknown values neutral', () => {
    expect(reportSeverityTone('Info')).toBe('neutral');
    expect(reportSeverityTone('Warn')).toBe('warning');
    expect(reportSeverityTone('High')).toBe('warning');
    expect(reportSeverityTone('Critical')).toBe('danger');
    expect(reportSeverityTone('Later')).toBe('neutral');
    expect(reportSeverityTone(null)).toBe('neutral');
  });

  it('maps finding severity, with unknown values neutral', () => {
    expect(findingSeverityTone('Info')).toBe('neutral');
    expect(findingSeverityTone('Warn')).toBe('warning');
    expect(findingSeverityTone('High')).toBe('danger');
    expect(findingSeverityTone('')).toBe('neutral');
  });

  it('maps parse status, with unknown values neutral', () => {
    expect(parseStatusTone('Structured')).toBe('success');
    expect(parseStatusTone('Unstructured')).toBe('warning');
    expect(parseStatusTone('MalformedJson')).toBe('danger');
    expect(parseStatusTone(undefined)).toBe('neutral');
  });
});
