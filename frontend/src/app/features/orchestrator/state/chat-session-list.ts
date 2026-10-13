import type { OrchestratorContextSession } from '../models/orchestrator.model';

/** Scope filter offered by the Chats sidebar panel. */
export type ChatSessionScopeFilter = 'all' | 'projects' | 'dossiers' | 'tasks';

/** Live state word shown in the Active group. */
export type ChatSessionLiveState = 'running' | 'queued' | 'waiting' | 'parked';

export interface ChatSessionRow {
  readonly contextKey: string;
  readonly kind: OrchestratorContextSession['kind'];
  /** Line 1: summary, else task title / Dossier title / project name. */
  readonly title: string;
  /** Line 2: project name, task key, or Dossier key. */
  readonly scopeLabel: string;
  /** Line 2: catalogue display name of the latest model, when known. */
  readonly modelLabel: string | null;
  /** Line 2: plain token total, e.g. `5.6m tok`; null when nothing was spent. */
  readonly tokensLabel: string | null;
  /** ISO timestamp the recency order and relative time are derived from. */
  readonly lastActivityAt: string;
  readonly timeLabel: string;
  readonly live: ChatSessionLiveState | null;
  readonly liveLabel: string | null;
  readonly unread: boolean;
  readonly archived: boolean;
  /** Operator archive is never offered while a turn is running, queued, or waiting. */
  readonly archivable: boolean;
}

export interface ChatSessionGroups {
  readonly active: readonly ChatSessionRow[];
  readonly recent: readonly ChatSessionRow[];
  readonly archived: readonly ChatSessionRow[];
}

export interface ChatSessionListInput {
  readonly sessions: readonly OrchestratorContextSession[];
  readonly pendingContextKeys: ReadonlySet<string>;
  readonly unreadContextKeys: ReadonlySet<string>;
  readonly query?: string;
  readonly scope?: ChatSessionScopeFilter;
  readonly now: number;
  readonly taskTitle?: (session: OrchestratorContextSession) => string | null;
  readonly workbenchTitle?: (session: OrchestratorContextSession) => string | null;
  readonly modelLabel?: (model: string) => string | null;
}

/**
 * A chat is listed only once it holds a persisted turn. Seeded project
 * contexts (`Project chat for X`) and contexts opened but never used have
 * `turnCount === 0`. `calls` is the older wire name for the same count.
 */
export function chatSessionHasTurns(session: OrchestratorContextSession): boolean {
  return (session.turnCount ?? session.calls ?? 0) > 0;
}

export function chatSessionLiveState(
  session: Pick<OrchestratorContextSession, 'contextKey' | 'runtimeStatus'>,
  pendingContextKeys: ReadonlySet<string>,
): ChatSessionLiveState | null {
  if (session.runtimeStatus === 'active') return 'running';
  if (session.runtimeStatus === 'queued') return 'queued';
  if (pendingContextKeys.has(session.contextKey)) return 'waiting';
  if (session.runtimeStatus === 'parked') return 'parked';
  return null;
}

export function chatSessionIsArchived(session: OrchestratorContextSession): boolean {
  return !!session.archivedAt || !!session.hiddenAt;
}

/** Projects every listable session into the Active / Recent / Archived groups. */
export function buildChatSessionGroups(input: ChatSessionListInput): ChatSessionGroups {
  const query = (input.query ?? '').trim().toLowerCase();
  const scope = input.scope ?? 'all';
  const active: ChatSessionRow[] = [];
  const recent: ChatSessionRow[] = [];
  const archived: ChatSessionRow[] = [];

  for (const session of input.sessions) {
    if (session.kind === 'global') continue;
    if (!matchesScope(session, scope)) continue;
    const live = chatSessionLiveState(session, input.pendingContextKeys);
    const working = live === 'running' || live === 'queued' || live === 'waiting';
    if (!chatSessionHasTurns(session) && !working) continue;

    const row = buildRow(session, live, input);
    if (query && !matchesQuery(session, row, query)) continue;

    if (working || (live === 'parked' && !row.archived)) active.push(row);
    else if (row.archived) archived.push(row);
    else recent.push(row);
  }

  return {
    active: active.sort(byRecency),
    recent: recent.sort(byRecency),
    archived: archived.sort(byRecency),
  };
}

function buildRow(
  session: OrchestratorContextSession,
  live: ChatSessionLiveState | null,
  input: ChatSessionListInput,
): ChatSessionRow {
  const lastActivityAt = session.lastUsedAt || session.updatedAt;
  const archived = chatSessionIsArchived(session);
  const working = live === 'running' || live === 'queued' || live === 'waiting';
  return {
    contextKey: session.contextKey,
    kind: session.kind,
    title: chatSessionTitle(session, input),
    scopeLabel: chatSessionScopeLabel(session),
    modelLabel: session.model ? (input.modelLabel?.(session.model) ?? fallbackModelLabel(session.model)) : null,
    tokensLabel: formatChatTokens(totalTokens(session)),
    lastActivityAt,
    timeLabel: formatRelativeTime(lastActivityAt, input.now),
    live,
    liveLabel: live === 'queued' && session.queuePosition > 0 ? `queued ${session.queuePosition}` : live,
    unread: !live && input.unreadContextKeys.has(session.contextKey),
    archived,
    archivable: !archived && !working,
  };
}

export function chatSessionTitle(
  session: OrchestratorContextSession,
  input: Pick<ChatSessionListInput, 'taskTitle' | 'workbenchTitle'> = {},
): string {
  const summary = session.summary?.trim();
  if (summary) return summary;
  if (session.kind === 'task') return input.taskTitle?.(session) ?? session.taskKey ?? session.contextKey;
  if (session.kind === 'workbench') {
    return input.workbenchTitle?.(session) ?? chatSessionWorkbenchKey(session) ?? session.contextKey;
  }
  if (session.kind === 'global') return 'Global orchestrator';
  return session.projectId ?? session.contextKey;
}

export function chatSessionScopeLabel(session: OrchestratorContextSession): string {
  switch (session.kind) {
    case 'task': return session.taskKey ?? session.projectId ?? session.contextKey;
    case 'workbench': return chatSessionWorkbenchKey(session) ?? session.projectId ?? session.contextKey;
    case 'global': return 'Global';
    default: return session.projectId ?? session.contextKey;
  }
}

/** Dossier key of a `workbench:<PROJ>/<KEY>` session; the wire field is optional. */
export function chatSessionWorkbenchKey(session: OrchestratorContextSession): string | null {
  if (session.workbenchKey) return session.workbenchKey;
  if (session.kind !== 'workbench') return null;
  const slash = session.contextKey.indexOf('/');
  return slash >= 0 && slash < session.contextKey.length - 1 ? session.contextKey.slice(slash + 1) : null;
}

/** `5.6m tok`, `56k tok`, `320 tok`; null when the session spent nothing. */
export function formatChatTokens(total: number): string | null {
  if (!Number.isFinite(total) || total <= 0) return null;
  if (total >= 1_000_000) return `${trimDecimal(total / 1_000_000)}m tok`;
  if (total >= 1_000) return `${total >= 100_000 ? Math.round(total / 1_000) : trimDecimal(total / 1_000)}k tok`;
  return `${Math.round(total)} tok`;
}

/** Compact relative time: `now`, `3m`, `4h`, `4d`, `3w`, then a short date. */
export function formatRelativeTime(iso: string, now: number): string {
  const timestamp = Date.parse(iso);
  if (!Number.isFinite(timestamp)) return '';
  const seconds = Math.max(0, Math.floor((now - timestamp) / 1000));
  if (seconds < 60) return 'now';
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d`;
  if (days < 35) return `${Math.floor(days / 7)}w`;
  const date = new Date(timestamp);
  const sameYear = date.getFullYear() === new Date(now).getFullYear();
  return new Intl.DateTimeFormat('en-US', sameYear
    ? { month: 'short', day: 'numeric' }
    : { month: 'short', year: 'numeric' }).format(date);
}

/** Raw-id fallback when the model is not in any loaded catalogue: `claude-opus-5-5` -> `Opus 5.5`. */
export function fallbackModelLabel(model: string): string {
  const id = model.trim();
  const claude = /^claude-([a-z]+)-(\d+)(?:-(\d+))?(?:-\d{8})?$/i.exec(id);
  if (claude) {
    const [, family, major, minor] = claude;
    return `${family[0].toUpperCase()}${family.slice(1).toLowerCase()} ${major}${minor ? `.${minor}` : ''}`;
  }
  const slash = id.lastIndexOf('/');
  return slash >= 0 && slash < id.length - 1 ? id.slice(slash + 1) : id;
}

function totalTokens(session: OrchestratorContextSession): number {
  return session.cumulativeInputTokens + session.cumulativeOutputTokens
    + session.cumulativeCacheReadTokens + session.cumulativeCacheCreationTokens;
}

function trimDecimal(value: number): string {
  return value >= 10 ? String(Math.round(value)) : String(Math.round(value * 10) / 10);
}

function matchesScope(session: OrchestratorContextSession, scope: ChatSessionScopeFilter): boolean {
  switch (scope) {
    case 'projects': return session.kind === 'project';
    case 'dossiers': return session.kind === 'workbench';
    case 'tasks': return session.kind === 'task';
    default: return true;
  }
}

function matchesQuery(session: OrchestratorContextSession, row: ChatSessionRow, query: string): boolean {
  return [row.title, row.scopeLabel, row.modelLabel, session.projectId, session.taskKey, session.contextKey]
    .some(value => !!value && value.toLowerCase().includes(query));
}

function byRecency(left: ChatSessionRow, right: ChatSessionRow): number {
  return (Date.parse(right.lastActivityAt) || 0) - (Date.parse(left.lastActivityAt) || 0)
    || left.contextKey.localeCompare(right.contextKey);
}
