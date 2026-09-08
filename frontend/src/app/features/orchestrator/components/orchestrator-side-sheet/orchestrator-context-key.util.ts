import type { OrchestratorContextSession } from '../../models/orchestrator.model';

export interface ParsedOrchestratorContextKey {
  key: string;
  kind: 'global' | 'project' | 'workbench' | 'task';
  projectId: string | null;
  taskKey: string | null;
  /** Dossier (Workbench) catalogue key, e.g. `AGT-W43`, for a workbench-kind key. */
  workbenchKey: string | null;
}

function validPart(value: string): boolean {
  return value.length > 0
    && value === value.trim()
    && !value.includes('/')
    && !value.includes('\\')
    && ![...value].some(char => {
      const code = char.charCodeAt(0);
      return code < 32 || (code >= 127 && code <= 159);
    });
}

/** Mirrors the strict backend OrchestratorContextKey parser. */
export function parseOrchestratorContextKey(raw: string | null | undefined): ParsedOrchestratorContextKey | null {
  if (!raw || raw !== raw.trim()) return null;
  if (raw === 'global') return { key: raw, kind: 'global', projectId: null, taskKey: null, workbenchKey: null };
  if (raw.startsWith('project:')) {
    const projectId = raw.slice('project:'.length);
    return validPart(projectId)
      ? { key: raw, kind: 'project', projectId, taskKey: null, workbenchKey: null }
      : null;
  }
  if (raw.startsWith('workbench:')) {
    const rest = raw.slice('workbench:'.length);
    const slash = rest.indexOf('/');
    if (slash < 0) return null;
    const projectId = rest.slice(0, slash);
    const workbenchKey = rest.slice(slash + 1);
    return validPart(projectId) && validPart(workbenchKey)
      ? { key: raw, kind: 'workbench', projectId, taskKey: null, workbenchKey }
      : null;
  }
  if (raw.startsWith('task:')) {
    const rest = raw.slice('task:'.length);
    const slash = rest.indexOf('/');
    if (slash < 0) return null;
    const projectId = rest.slice(0, slash);
    const taskKey = rest.slice(slash + 1);
    return validPart(projectId) && validPart(taskKey)
      ? { key: raw, kind: 'task', projectId, taskKey, workbenchKey: null }
      : null;
  }
  return null;
}

export function buildNavigationContextKey(project: string | null, taskKey: string | null): string | null {
  const canonicalProject = project?.trim() ?? '';
  const canonicalTask = taskKey?.trim() ?? '';
  if (!validPart(canonicalProject)) return null;
  if (canonicalTask && validPart(canonicalTask)) return `task:${canonicalProject}/${canonicalTask}`;
  return `project:${canonicalProject}`;
}

/** Builds a `workbench:<PROJ>/<DOSSIER-KEY>` context key for a Dossier session. */
export function buildWorkbenchContextKey(project: string | null, workbenchKey: string | null): string | null {
  const canonicalProject = project?.trim() ?? '';
  const canonicalWorkbench = workbenchKey?.trim() ?? '';
  if (!validPart(canonicalProject) || !validPart(canonicalWorkbench)) return null;
  return `workbench:${canonicalProject}/${canonicalWorkbench}`;
}

export interface EffectiveContextKeyResult {
  key: string | null;
  discardedSelection: boolean;
}

/**
 * Resolve the one key shared by transcript reads, digest reads, refreshes, and
 * sends. A chat-switcher selection is valid only while navigation remains at
 * the scope where it was selected and the selected scope still exists.
 */
export function resolveEffectiveContextKey(
  navigationKey: string | null,
  selectedKey: string | null,
  selectionNavigationKey: string | null,
  projects: readonly string[],
  sessions: readonly OrchestratorContextSession[],
): EffectiveContextKeyResult {
  const navigation = parseOrchestratorContextKey(navigationKey);
  if (!selectedKey) return { key: navigation?.key ?? null, discardedSelection: false };

  const selected = parseOrchestratorContextKey(selectedKey);
  const selectionStillAnchored = selectionNavigationKey === (navigation?.key ?? null);
  const selectedStillExists = selected?.kind === 'global'
    || (selected?.kind === 'project' && projects.includes(selected.projectId!))
    || (selected?.kind === 'task' && sessions.some(session =>
      session.contextKey === selected.key
      && session.kind === 'task'
      && session.projectId === selected.projectId
      && session.taskKey === selected.taskKey))
    // A Dossier session is not derived from the watched-project list like
    // 'project' is; it exists once the registry (sessions) carries it,
    // exactly like 'task' above.
    || (selected?.kind === 'workbench' && sessions.some(session =>
      session.contextKey === selected.key
      && session.kind === 'workbench'
      && session.projectId === selected.projectId
      && session.workbenchKey === selected.workbenchKey));

  if (!selected || !selectionStillAnchored || !selectedStillExists) {
    return { key: navigation?.key ?? null, discardedSelection: true };
  }
  return { key: selected.key, discardedSelection: false };
}

export function orchestratorContextErrorMessage(error: unknown, fallback: string): string {
  const candidate = error as { error?: { error?: unknown }; message?: unknown } | null;
  const rawMessage = candidate?.error?.error ?? candidate?.message;
  const technical = typeof rawMessage === 'string' && rawMessage.trim() ? rawMessage : fallback;
  return technical.includes('Invalid orchestrator context key')
    ? 'This chat context is no longer available. Return to the current task or board, then try again.'
    : technical;
}
