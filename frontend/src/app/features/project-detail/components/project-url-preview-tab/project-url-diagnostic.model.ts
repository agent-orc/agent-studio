import type { ProjectUrlDiagnostic, ProjectUrlDiagnosisClass } from '../../../../models/task.model';
import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';

export interface ProjectUrlDiagnosisPresentation {
  status: string;
  title: string;
  primaryAction: 'start' | 'retry' | 'settings' | 'external';
}

const PRESENTATION: Record<ProjectUrlDiagnosisClass, ProjectUrlDiagnosisPresentation> = {
  'not-started': { status: 'Offline', title: 'The preview service is not running', primaryAction: 'start' },
  starting: { status: 'Starting', title: 'The process is working — waiting for the URL…', primaryAction: 'retry' },
  'command-unavailable': { status: 'Start failed', title: 'The start command is unavailable', primaryAction: 'settings' },
  'invalid-cwd': { status: 'Setup issue', title: 'The working directory is invalid', primaryAction: 'settings' },
  'process-exited': { status: 'Start failed', title: 'The service exited during startup', primaryAction: 'retry' },
  'port-in-use': { status: 'Port occupied', title: 'Another process owns the preview port', primaryAction: 'retry' },
  'port-never-opened': { status: 'Not ready', title: 'The configured port never opened', primaryAction: 'settings' },
  timeout: { status: 'Timed out', title: 'Readiness could not be confirmed', primaryAction: 'retry' },
  'http-error-response': { status: 'HTTP error', title: 'The server returned an error response', primaryAction: 'settings' },
  'content-not-renderable': { status: 'Cannot preview', title: 'The response cannot be rendered here', primaryAction: 'external' },
  'invalid-configuration': { status: 'Setup needed', title: 'URL Preview is not configured correctly', primaryAction: 'settings' },
  running: { status: 'Running', title: 'Preview ready', primaryAction: 'retry' },
  'local-only': { status: 'Local only', title: 'This URL is only reachable on the machine that serves it', primaryAction: 'external' },
};

/** Status tone of each diagnosis: a setup or readiness problem needs attention, a failed start is danger. */
const DIAGNOSIS_TONES: Readonly<Record<ProjectUrlDiagnosisClass, StatusTone>> = {
  'not-started': 'neutral',
  starting: 'running',
  'command-unavailable': 'danger',
  'invalid-cwd': 'warning',
  'process-exited': 'danger',
  'port-in-use': 'warning',
  'port-never-opened': 'warning',
  timeout: 'warning',
  'http-error-response': 'danger',
  'content-not-renderable': 'warning',
  'invalid-configuration': 'warning',
  running: 'success',
  'local-only': 'info',
};

export function projectUrlDiagnosisTone(value: ProjectUrlDiagnostic | null): StatusTone {
  return DIAGNOSIS_TONES[value?.classification ?? 'not-started'] ?? 'neutral';
}

export function presentProjectUrlDiagnosis(value: ProjectUrlDiagnostic | null): ProjectUrlDiagnosisPresentation {
  return PRESENTATION[value?.classification ?? 'not-started'];
}

export function diagnosticText(value: ProjectUrlDiagnostic): string {
  return [
    `Diagnosis: ${value.classification}`,
    `Summary: ${value.summary}`,
    `URL: ${value.url ?? '(none)'}`,
    `Command: ${value.command ?? '(none)'}`,
    `Working directory: ${value.cwd ?? '(project root)'}`,
    `Port: ${value.configuredPort ?? '(from URL)'}`,
    `Process created: ${value.processCreated}`,
    `Exit code: ${value.exitCode ?? '(none)'}`,
    `Port reachable: ${value.portReachable}`,
    `HTTP status: ${value.httpStatus ?? '(none)'}`,
    `Content ready: ${value.contentReady}`,
    value.occupyingProcessName && value.occupyingProcessId
      ? `Port owner: ${value.occupyingProcessName} (PID ${value.occupyingProcessId})`
      : '',
    `Iframe ready: ${value.iframeReady ?? '(not checked)'}`,
    value.framePolicy ? `Frame policy: ${value.framePolicy}` : '',
    value.stdoutTail ? `stdout (tail):\n${value.stdoutTail}` : '',
    value.stderrTail ? `stderr (tail):\n${value.stderrTail}` : '',
  ].filter(Boolean).join('\n');
}

/** Redact and bound configured values used only when the backend diagnostic
 * request itself failed before it could return its redacted contract. */
export function redactDiagnosticValue(raw: string | null | undefined): string | null {
  if (raw == null) return null;
  const redacted = raw
    .replace(/^(https?:\/\/)[^/\s:@]+(?::[^/\s@]*)?@/i, '$1[REDACTED]@')
    .replace(/(bearer\s+)[a-z0-9._~+/=-]+/gi, '$1[REDACTED]')
    .replace(/((?:api[_-]?key|token|password|secret|authorization)\s*[:=]\s*)(?:bearer\s+)?[^\s\r\n]+/gi, '$1[REDACTED]');
  return redacted.length <= 8192 ? redacted : `…${redacted.slice(-8192)}`;
}

/** Keep credentials and common secret query values out of preview chrome. */
export function safePreviewUrl(raw: string | null | undefined): string {
  if (!raw) return '…';
  return raw
    .replace(/^(https?:\/\/)[^/\s:@]+(?::[^/\s@]*)?@/i, '$1[REDACTED]@')
    .replace(/([?&](?:api[_-]?key|token|password|secret|authorization)=)[^&\s]*/gi, '$1[REDACTED]');
}
