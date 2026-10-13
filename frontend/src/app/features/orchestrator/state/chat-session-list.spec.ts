import { describe, expect, it } from 'vitest';
import type { OrchestratorContextSession } from '../models/orchestrator.model';
import {
  buildChatSessionGroups,
  chatSessionHasTurns,
  chatSessionLiveState,
  fallbackModelLabel,
  formatChatTokens,
  formatRelativeTime,
} from './chat-session-list';

const NOW = Date.parse('2026-10-11T12:00:00Z');

function session(overrides: Partial<OrchestratorContextSession> & Pick<OrchestratorContextSession, 'contextKey'>): OrchestratorContextSession {
  const kind = overrides.kind ?? (overrides.contextKey.startsWith('task:') ? 'task'
    : overrides.contextKey.startsWith('workbench:') ? 'workbench' : 'project');
  return {
    kind,
    projectId: 'Agent Studio',
    taskKey: null,
    updatedAt: '2026-10-11T11:00:00Z',
    model: null,
    cumulativeInputTokens: 0,
    cumulativeOutputTokens: 0,
    cumulativeCacheReadTokens: 0,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'idle',
    queuePosition: 0,
    turnCount: 1,
    ...overrides,
  };
}

function groups(sessions: OrchestratorContextSession[], extra: Partial<Parameters<typeof buildChatSessionGroups>[0]> = {}) {
  return buildChatSessionGroups({
    sessions,
    pendingContextKeys: new Set(),
    unreadContextKeys: new Set(),
    now: NOW,
    ...extra,
  });
}

describe('chat session list: empty sessions', () => {
  it('hides a seeded project chat that never received a turn', () => {
    const result = groups([
      session({ contextKey: 'project:Fresh', projectId: 'Fresh', turnCount: 0, summary: 'Project chat for Fresh' }),
      session({ contextKey: 'project:Used', projectId: 'Used', turnCount: 4, summary: 'Why is the runner idle?' }),
    ]);

    expect(result.recent.map(row => row.contextKey)).toEqual(['project:Used']);
    expect(result.active).toEqual([]);
    expect(result.archived).toEqual([]);
  });

  it('falls back to the legacy calls count and treats a missing count as empty', () => {
    expect(chatSessionHasTurns(session({ contextKey: 'project:A', turnCount: undefined, calls: 3 }))).toBe(true);
    expect(chatSessionHasTurns(session({ contextKey: 'project:A', turnCount: undefined, calls: undefined }))).toBe(false);
  });

  it('still lists a context whose first turn is running', () => {
    const result = groups([session({ contextKey: 'project:A', turnCount: 0, runtimeStatus: 'active' })]);
    expect(result.active.map(row => row.contextKey)).toEqual(['project:A']);
  });

  it('never lists the synthetic global context', () => {
    expect(groups([session({ contextKey: 'global', kind: 'global', projectId: null })]).recent).toEqual([]);
  });
});

describe('chat session list: grouping and ordering', () => {
  it('puts running, queued, waiting, and parked sessions in Active with a state word', () => {
    const result = groups([
      session({ contextKey: 'project:Running', runtimeStatus: 'active' }),
      session({ contextKey: 'project:Queued', runtimeStatus: 'queued', queuePosition: 2 }),
      session({ contextKey: 'project:Waiting' }),
      session({ contextKey: 'project:Parked', runtimeStatus: 'parked' }),
      session({ contextKey: 'project:Idle' }),
    ], { pendingContextKeys: new Set(['project:Waiting']) });

    const labels = Object.fromEntries(result.active.map(row => [row.contextKey, row.liveLabel]));
    expect(labels).toEqual({
      'project:Running': 'running',
      'project:Queued': 'queued 2',
      'project:Waiting': 'waiting',
      'project:Parked': 'parked',
    });
    expect(result.recent.map(row => row.contextKey)).toEqual(['project:Idle']);
  });

  it('moves a session out of Active once it is idle again', () => {
    const running = session({ contextKey: 'project:A', runtimeStatus: 'active' });
    expect(groups([running]).active).toHaveLength(1);
    const idle = { ...running, runtimeStatus: 'idle' as const };
    expect(groups([idle]).active).toHaveLength(0);
    expect(groups([idle]).recent).toHaveLength(1);
  });

  it('sorts by lastUsedAt, falling back to updatedAt, newest first', () => {
    const result = groups([
      session({ contextKey: 'project:Old', updatedAt: '2026-10-01T00:00:00Z' }),
      session({ contextKey: 'project:New', updatedAt: '2026-10-01T00:00:00Z', lastUsedAt: '2026-10-11T11:59:00Z' }),
      session({ contextKey: 'project:Mid', updatedAt: '2026-10-09T00:00:00Z' }),
    ]);
    expect(result.recent.map(row => row.contextKey)).toEqual(['project:New', 'project:Mid', 'project:Old']);
  });

  it('groups operator-archived and task-archive-hidden sessions under Archived', () => {
    const result = groups([
      session({ contextKey: 'project:Archived', archivedAt: '2026-10-10T00:00:00Z' }),
      session({ contextKey: 'task:Agent Studio/AGT-1', taskKey: 'AGT-1', hiddenAt: '2026-10-09T00:00:00Z' }),
      session({ contextKey: 'project:Live' }),
    ]);
    expect(result.archived.map(row => row.contextKey).sort())
      .toEqual(['project:Archived', 'task:Agent Studio/AGT-1']);
    expect(result.archived.every(row => row.archived && !row.archivable)).toBe(true);
    expect(result.recent.map(row => row.contextKey)).toEqual(['project:Live']);
  });

  it('keeps a running archived session in Active and never offers archive while it works', () => {
    const result = groups([
      session({ contextKey: 'project:A', archivedAt: '2026-10-10T00:00:00Z', runtimeStatus: 'active' }),
      session({ contextKey: 'project:B', runtimeStatus: 'queued', queuePosition: 1 }),
    ]);
    expect(result.active.map(row => row.contextKey)).toEqual(['project:A', 'project:B']);
    expect(result.active.every(row => !row.archivable)).toBe(true);
  });

  it('filters by scope and search text', () => {
    const sessions = [
      session({ contextKey: 'project:Agent Studio', summary: 'Release notes' }),
      session({ contextKey: 'task:Agent Studio/AGT-7', taskKey: 'AGT-7', summary: 'Fix the runner' }),
      session({ contextKey: 'workbench:Agent Studio/AGT-W4', summary: 'Dossier review' }),
    ];
    expect(groups(sessions, { scope: 'tasks' }).recent.map(row => row.contextKey)).toEqual(['task:Agent Studio/AGT-7']);
    expect(groups(sessions, { scope: 'dossiers' }).recent.map(row => row.scopeLabel)).toEqual(['AGT-W4']);
    expect(groups(sessions, { query: 'agt-7' }).recent.map(row => row.contextKey)).toEqual(['task:Agent Studio/AGT-7']);
    expect(groups(sessions, { query: 'release' }).recent.map(row => row.contextKey)).toEqual(['project:Agent Studio']);
  });
});

describe('chat session list: row labels', () => {
  it('builds title, scope, model display name, tokens, and unread from one session', () => {
    const [row] = groups([session({
      contextKey: 'task:Agent Studio/AGT-9',
      taskKey: 'AGT-9',
      summary: '  ',
      model: 'gpt-5.5',
      cumulativeInputTokens: 5_000_000,
      cumulativeOutputTokens: 600_000,
    })], {
      unreadContextKeys: new Set(['task:Agent Studio/AGT-9']),
      taskTitle: () => 'Chat sessions in the sidebar',
      modelLabel: model => (model === 'gpt-5.5' ? 'GPT-5.5' : null),
    }).recent;

    expect(row.title).toBe('Chat sessions in the sidebar');
    expect(row.scopeLabel).toBe('AGT-9');
    expect(row.modelLabel).toBe('GPT-5.5');
    expect(row.tokensLabel).toBe('5.6m tok');
    expect(row.timeLabel).toBe('1h');
    expect(row.unread).toBe(true);
  });

  it('uses the project name and Dossier key when there is no summary', () => {
    const result = groups([
      session({ contextKey: 'project:Agent Studio', summary: null }),
      session({ contextKey: 'workbench:Agent Studio/AGT-W43', summary: null }),
    ]);
    const titles = Object.fromEntries(result.recent.map(row => [row.contextKey, row.title]));
    expect(titles['project:Agent Studio']).toBe('Agent Studio');
    expect(titles['workbench:Agent Studio/AGT-W43']).toBe('AGT-W43');
  });

  it('maps a raw Claude id to a short display name when no catalogue knows it', () => {
    expect(fallbackModelLabel('claude-opus-5-5')).toBe('Opus 5.5');
    expect(fallbackModelLabel('claude-haiku-4-5-20251001')).toBe('Haiku 4.5');
    expect(fallbackModelLabel('openai/gpt-5.5')).toBe('gpt-5.5');
  });

  it('formats tokens and relative time compactly', () => {
    expect(formatChatTokens(0)).toBeNull();
    expect(formatChatTokens(320)).toBe('320 tok');
    expect(formatChatTokens(5_600)).toBe('5.6k tok');
    expect(formatChatTokens(250_000)).toBe('250k tok');
    expect(formatRelativeTime('2026-10-11T11:59:30Z', NOW)).toBe('now');
    expect(formatRelativeTime('2026-10-11T11:57:00Z', NOW)).toBe('3m');
    expect(formatRelativeTime('2026-10-07T12:00:00Z', NOW)).toBe('4d');
    expect(formatRelativeTime('2026-09-20T12:00:00Z', NOW)).toBe('3w');
  });

  it('prefers running over queued over waiting over parked', () => {
    const pending = new Set(['project:A']);
    expect(chatSessionLiveState({ contextKey: 'project:A', runtimeStatus: 'active' }, pending)).toBe('running');
    expect(chatSessionLiveState({ contextKey: 'project:A', runtimeStatus: 'queued' }, pending)).toBe('queued');
    expect(chatSessionLiveState({ contextKey: 'project:A', runtimeStatus: 'parked' }, pending)).toBe('waiting');
    expect(chatSessionLiveState({ contextKey: 'project:B', runtimeStatus: 'parked' }, pending)).toBe('parked');
    expect(chatSessionLiveState({ contextKey: 'project:B', runtimeStatus: 'idle' }, pending)).toBeNull();
  });
});
