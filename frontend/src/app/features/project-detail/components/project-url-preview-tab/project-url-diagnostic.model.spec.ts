import { describe, expect, it } from 'vitest';
import type { ProjectUrlDiagnostic, ProjectUrlDiagnosisClass } from '../../../../models/task.model';
import {
  diagnosticText,
  presentProjectUrlDiagnosis,
  projectUrlDiagnosisTone,
  redactDiagnosticValue,
  safePreviewUrl,
} from './project-url-diagnostic.model';

function diagnostic(classification: ProjectUrlDiagnosisClass): ProjectUrlDiagnostic {
  return {
    classification, summary: 'summary', recommendedAction: 'action', command: 'npm start', cwd: '/repo',
    url: 'http://127.0.0.1:4184', configuredPort: 4184, processCreated: true, exitCode: null,
    stdoutTail: '', stderrTail: '', timedOut: false, portReachable: false, httpStatus: null,
    contentReady: false, checkedAt: '2026-07-13T00:00:00Z',
  };
}

describe('URL Preview diagnosis presentation', () => {
  it.each([
    ['not-started', 'start'], ['starting', 'retry'], ['command-unavailable', 'settings'],
    ['invalid-cwd', 'settings'], ['process-exited', 'retry'], ['port-in-use', 'retry'], ['port-never-opened', 'settings'],
    ['timeout', 'retry'], ['http-error-response', 'settings'], ['content-not-renderable', 'external'],
    ['invalid-configuration', 'settings'], ['running', 'retry'],
  ] as const)('maps %s to its recovery action', (classification, action) => {
    expect(presentProjectUrlDiagnosis(diagnostic(classification)).primaryAction).toBe(action);
  });

  it.each([
    ['not-started', 'neutral'], ['starting', 'running'], ['command-unavailable', 'danger'],
    ['invalid-cwd', 'warning'], ['process-exited', 'danger'], ['port-in-use', 'warning'], ['port-never-opened', 'warning'],
    ['timeout', 'warning'], ['http-error-response', 'danger'], ['content-not-renderable', 'warning'],
    ['invalid-configuration', 'warning'], ['running', 'success'], ['local-only', 'info'],
  ] as const)('maps %s to the %s status tone', (classification, tone) => {
    expect(projectUrlDiagnosisTone(diagnostic(classification))).toBe(tone);
  });

  it('builds copyable diagnostics from bounded structured evidence', () => {
    const text = diagnosticText({
      ...diagnostic('process-exited'), exitCode: 127, stderrTail: 'command not found',
      iframeReady: false, framePolicy: 'X-Frame-Options: DENY',
    });
    expect(text).toContain('Diagnosis: process-exited');
    expect(text).toContain('Exit code: 127');
    expect(text).toContain('command not found');
    expect(text).toContain('X-Frame-Options: DENY');
  });

  it('includes the occupying process and PID in copied diagnostics', () => {
    const text = diagnosticText({
      ...diagnostic('port-in-use'), occupyingProcessName: 'marketing-app', occupyingProcessId: 9123,
    });
    expect(text).toContain('Port owner: marketing-app (PID 9123)');
  });

  it('redacts URL credentials and secret query values for chrome', () => {
    expect(safePreviewUrl('https://user:pass@example.test/page?token=secret&view=1'))
      .toBe('https://[REDACTED]@example.test/page?token=[REDACTED]&view=1');
  });

  it('redacts and bounds frontend fallback diagnostic values', () => {
    const value = redactDiagnosticValue(`npm start -- --token=secret Bearer abc.def ${'x'.repeat(9000)}`);
    expect(value).not.toContain('secret');
    expect(value).not.toContain('abc.def');
    expect(value?.length).toBeLessThanOrEqual(8193);
  });
});
