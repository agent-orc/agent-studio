import type { MenuItem } from '../../../../components/menu';
import type { ProjectUrlProcessSnapshot, RegistryProjectUrl } from '../../../../models/task.model';

/** Backend refusal (or readiness timeout) surfaced on the state card. */
export interface StartFailure {
  explanation: string;
  command: string;
  cwd: string;
}

export function projectPreviewProxyUrl(projectId: string | null, urlId: string): string | null {
  return projectId ? `/api/projects/${encodeURIComponent(projectId)}/urls/${encodeURIComponent(urlId)}/preview/` : null;
}

/** Builds the embed action menu for the preview tab's `⋯` trigger. */
export function buildEmbedMenuItems(state: {
  url: RegistryProjectUrl | null;
  session: ProjectUrlProcessSnapshot | null;
  probeRunning: boolean;
  building: boolean;
  stopping: boolean;
}): readonly MenuItem[] {
  const ownsRunning = state.session?.state === 'running' || state.session?.state === 'starting';
  const hasStartRule = Boolean(state.url?.startRule);
  return [
    { kind: 'header', label: state.url?.label ?? 'Embed' },
    {
      kind: 'row',
      id: 'start',
      label: state.probeRunning || ownsRunning ? 'Restart' : 'Start',
      hint: state.url?.startRule?.command,
      disabled: !hasStartRule || state.building || state.stopping,
    },
    { kind: 'row', id: 'console', label: 'Show live console', disabled: !state.session },
    { kind: 'row', id: 'stop', label: 'Stop server', danger: true, disabled: !ownsRunning || state.stopping },
    { kind: 'separator' },
    { kind: 'row', id: 'settings', label: 'Embed settings', disabled: !state.url },
    { kind: 'row', id: 'external', label: 'Open externally', disabled: !state.url },
  ];
}

/** Best-effort operator message from an HTTP error payload. */
export function httpErrorMessage(error: unknown): string {
  const value = error as { error?: string | { error?: string; message?: string }; message?: string };
  if (typeof value?.error === 'string') return value.error;
  return value?.error?.error ?? value?.error?.message ?? value?.message ?? 'The operation failed.';
}
