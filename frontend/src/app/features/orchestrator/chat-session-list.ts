import { shortModelLabel } from 'coding-agent-chat/core';
import type { OrchestratorContextSession } from './models/orchestrator.model';

/**
 * Pure projection behind the left-sidebar chat session manager (AGT-3032).
 * Sessions without persisted turns never become rows; live sessions form the
 * Active group, archived ones (operator mark or task archive) the Archived
 * group, and everything else the Recent group, newest activity first.
 */

export type ChatScopeFilter = 'all' | 'projects' | 'dossiers' | 'tasks';
export type ChatSessionLiveState = 'running' | 'queued' | 'waiting' | 'parked';

export interface ChatSessionRow {
  readonly contextKey: string;
  readonly kind: OrchestratorContextSession['kind'];
  readonly title: string;
  readonly scopeLabel: string;
  readonly modelLabel: string | null;
  readonly tokenLabel: string | null;
  readonly timeLabel: string;
  readonly timeTitle: string;
  readonly live: ChatSessionLiveState | null;
  readonly liveLabel: string | null;
  readonly unread: boolean;
  readonly archived: boolean;
  /** Archive is not offered while a turn is running or queued. */
  readonly canArchive: boolean;
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
  readonly query: string;
  readonly scope: ChatScopeFilter;
  readonly now: number;
  /** Task title by `projectId/taskKey`; a missing title falls back to the key. */
  readonly taskTitle: (projectId: string | null, taskKey: string | null) => string | null;
  /** Dossier title by context key; a missing title falls back to the key. */
  readonly workbenchTitle: (contextKey: string) => string | null;
  /** Catalogue display name by model id; null when the catalogue has no entry. */
  readonly modelName: (model: string) => string | null;
}

export function hasPersistedTurns(session: OrchestratorContextSession): boolean {
  const turns = session.turnCount ?? session.calls;
  // A backend that predates the turn count cannot be filtered; keep its rows.
  return turns === undefined || turns > 0;
}

export function sessionLiveState(
  session: OrchestratorContextSession,
  pendingContextKeys: ReadonlySet<string>,
): ChatSessionLiveState | null {
  if (session.runtimeStatus === 'active') return 'running';
  if (session.runtimeStatus === 'queued') return 'queued';
  if (pendingContextKeys.has(session.contextKey)) return 'waiting';
  if (session.runtimeStatus === 'parked') return 'parked';
  return null;
}

export function sessionLastActivity(session: OrchestratorContextSession): string {
  return session.lastUsedAt || session.updatedAt || session.createdAt || '';
}

export function buildChatSessionGroups(input: ChatSessionListInput): ChatSessionGroups {
  const query = input.query.trim().toLowerCase();
  const active: ChatSessionRow[] = [];
  const recent: ChatSessionRow[] = [];
  const archived: ChatSessionRow[] = [];
  const ordered = [...input.sessions].sort((left, right) =>
    sessionLastActivity(right).localeCompare(sessionLastActivity(left))
    || left.contextKey.localeCompare(right.contextKey));

  for (const session of ordered) {
    const live = sessionLiveState(session, input.pendingContextKeys);
    // A session that is producing its first turn is live before it is persisted.
    if (!live && !hasPersistedTurns(session)) continue;
    if (!matchesScope(session, input.scope)) continue;
    const row = buildRow(session, live, input);
    if (query && !matchesQuery(row, session, query)) continue;
    if (row.live) active.push(row);
    else if (row.archived) archived.push(row);
    else recent.push(row);
  }
  return { active, recent, archived };
}

function buildRow(
  session: OrchestratorContextSession,
  live: ChatSessionLiveState | null,
  input: ChatSessionListInput,
): ChatSessionRow {
  const lastActivity = sessionLastActivity(session);
  return {
    contextKey: session.contextKey,
    kind: session.kind,
    title: sessionTitle(session, input),
    scopeLabel: sessionScopeLabel(session),
    modelLabel: session.model ? (input.modelName(session.model) ?? modelDisplayName(session.model)) : null,
    tokenLabel: sessionTokenLabel(session),
    timeLabel: relativeTimeLabel(lastActivity, input.now),
    timeTitle: absoluteTimeLabel(lastActivity),
    live,
    liveLabel: live === 'queued' ? `queued ${Math.max(1, session.queuePosition)}` : live,
    unread: input.unreadContextKeys.has(session.contextKey),
    archived: !!(session.archivedAt || session.hiddenAt),
    canArchive: session.runtimeStatus !== 'active' && session.runtimeStatus !== 'queued'
      && !input.pendingContextKeys.has(session.contextKey),
  };
}

function sessionTitle(session: OrchestratorContextSession, input: ChatSessionListInput): string {
  const summary = session.summary?.trim();
  if (summary) return summary;
  switch (session.kind) {
    case 'global':
      return 'Global orchestrator';
    case 'task':
      return input.taskTitle(session.projectId, session.taskKey) ?? session.taskKey ?? session.contextKey;
    case 'workbench':
      return input.workbenchTitle(session.contextKey) ?? session.workbenchKey ?? session.contextKey;
    default:
      return session.projectId ?? session.contextKey;
  }
}

function sessionScopeLabel(session: OrchestratorContextSession): string {
  switch (session.kind) {
    case 'global':
      return 'Global';
    case 'task':
      return session.taskKey ?? session.projectId ?? 'Task';
    case 'workbench':
      return session.workbenchKey ?? session.projectId ?? 'Dossier';
    default:
      return session.projectId ?? 'Project';
  }
}

function matchesScope(session: OrchestratorContextSession, scope: ChatScopeFilter): boolean {
  switch (scope) {
    case 'projects': return session.kind === 'project';
    case 'dossiers': return session.kind === 'workbench';
    case 'tasks': return session.kind === 'task';
    default: return true;
  }
}

function matchesQuery(row: ChatSessionRow, session: OrchestratorContextSession, query: string): boolean {
  return [row.title, row.scopeLabel, session.projectId, session.contextKey, row.modelLabel]
    .some(value => value?.toLowerCase().includes(query));
}

/** "claude-opus-5-5" -> "Opus 5.5", "gpt-5.4" -> "GPT-5.4" when the catalogue has no label. */
export function modelDisplayName(model: string): string {
  const short = shortModelLabel(model);
  return short
    .split(' ')
    .map(word => /^gpt/i.test(word)
      ? word.replace(/^gpt/i, 'GPT')
      : word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ');
}

export function sessionTokenLabel(session: OrchestratorContextSession): string | null {
  const total = session.cumulativeInputTokens + session.cumulativeOutputTokens
    + session.cumulativeCacheReadTokens + session.cumulativeCacheCreationTokens;
  if (!Number.isFinite(total) || total <= 0) return null;
  if (total >= 1_000_000) return `${(total / 1_000_000).toFixed(1)}m tok`;
  if (total >= 1_000) return `${Math.round(total / 1_000)}k tok`;
  return `${total} tok`;
}

/** Compact age for the fixed right-aligned time column: "now", "3m", "5h", "4d", "2w", "3mo", "1y". */
export function relativeTimeLabel(value: string, now: number): string {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) return '';
  const minutes = Math.floor(Math.max(0, now - timestamp) / 60_000);
  if (minutes < 1) return 'now';
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d`;
  if (days < 30) return `${Math.floor(days / 7)}w`;
  if (days < 365) return `${Math.floor(days / 30)}mo`;
  return `${Math.floor(days / 365)}y`;
}

function absoluteTimeLabel(value: string): string {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp)
    ? new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(timestamp))
    : '';
}
