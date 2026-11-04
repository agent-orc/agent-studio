import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { ProjectUrlReadiness } from '../../../../services/project-url-probe.service';

export type PreviewPill = 'running' | 'offline' | 'checking' | 'building' | 'blocked' | 'failed' | 'local-only' | 'custom';

const PREVIEW_PILL_TONES: Readonly<Record<PreviewPill, StatusTone>> = {
  running: 'success',
  building: 'running',
  checking: 'running',
  blocked: 'warning',
  failed: 'danger',
  offline: 'neutral',
  'local-only': 'info',
  custom: 'neutral',
};

/** Tone of the preview status (address bar pill and state card badge); unknown reads neutral. */
export function previewPillTone(status: string): StatusTone {
  return PREVIEW_PILL_TONES[status as PreviewPill] ?? 'neutral';
}

/** Facts the state card badge reads when the preview is not embedded. */
export interface PreviewStateBadgeInput {
  startFailed: boolean;
  building: boolean;
  consoleActive: boolean;
  startupFailed: boolean;
  readiness: ProjectUrlReadiness;
}

/** Status of the state card badge. */
export function previewStateBadgeStatus(i: PreviewStateBadgeInput): PreviewPill {
  if (i.startFailed) return 'failed';
  if (i.building) return 'building';
  if (i.startupFailed) return 'failed';
  if (i.readiness.kind === 'frame-blocked') return 'blocked';
  if (i.readiness.kind === 'http-error') return 'failed';
  return i.readiness.kind === 'local-only' ? 'local-only' : 'offline';
}

/** Visible word of the state card badge, read with {@link previewStateBadgeStatus}. */
export function previewStateBadgeLabel(i: PreviewStateBadgeInput): string {
  if (i.startFailed) return 'Failed';
  if (i.building) return i.consoleActive ? 'Console active' : 'Waiting';
  if (i.startupFailed) return 'Failed';
  const readiness = i.readiness;
  if (readiness.kind === 'frame-blocked') return 'Blocked';
  if (readiness.kind === 'http-error') return `HTTP ${readiness.statusCode}`;
  return readiness.kind === 'local-only' ? 'Local only' : 'Offline';
}
