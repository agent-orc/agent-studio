import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { ProjectUrlDiagnosisClass } from '../../../../models/task.model';
import type { ProjectUrlStatus } from '../../../../services/project-url-probe.service';

/** Per-URL pill state: the probe status, upgraded to "building" while a start/restart is in flight. */
export type UrlPill = ProjectUrlStatus | 'building';

/** Tone of one URL row status (AGT-3403). Offline is a dev server that is simply not started. */
const URL_PILL_TONES: Readonly<Record<UrlPill, StatusTone>> = {
  running: 'success',
  building: 'running',
  failed: 'danger',
  blocked: 'warning',
  offline: 'neutral',
  'local-only': 'neutral',
  unknown: 'neutral',
};

export function urlPillTone(pill: string): StatusTone {
  return URL_PILL_TONES[pill as UrlPill] ?? 'neutral';
}

/** Tone of the overall "N/M dev servers running" strip. */
export function urlStripTone(running: number, startable: number, anyBuilding: boolean): StatusTone {
  if (anyBuilding) return 'running';
  if (startable > 0 && running >= startable) return 'success';
  return 'neutral';
}

/** Tone of a test-setup diagnosis classification. */
const DIAGNOSIS_TONES: Readonly<Record<ProjectUrlDiagnosisClass, StatusTone>> = {
  running: 'success',
  starting: 'running',
  'not-started': 'neutral',
  'local-only': 'neutral',
  'command-unavailable': 'danger',
  'invalid-cwd': 'danger',
  'process-exited': 'danger',
  'http-error-response': 'danger',
  'port-in-use': 'warning',
  'port-never-opened': 'warning',
  timeout: 'warning',
  'content-not-renderable': 'warning',
  'invalid-configuration': 'warning',
};

export function urlDiagnosisTone(classification: string): StatusTone {
  return DIAGNOSIS_TONES[classification as ProjectUrlDiagnosisClass] ?? 'neutral';
}
