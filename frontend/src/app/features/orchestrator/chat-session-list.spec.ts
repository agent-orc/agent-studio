import { describe, expect, it } from 'vitest';
import {
  buildChatSessionGroups,
  modelDisplayName,
  relativeTimeLabel,
  type ChatSessionListInput,
} from './chat-session-list';
import type { OrchestratorContextSession } from './models/orchestrator.model';

const NOW = Date.parse('2026-10-10T12:00:00Z');

function session(overrides: Partial<OrchestratorContextSession> & { contextKey: string }): OrchestratorContextSession {
  return {
    kind: 'project',
    projectId: 'Agent Studio',
    taskKey: null,
    updatedAt: '2026-10-10T11:00:00Z',
    model: null,
    cumulativeInputTokens: 0,
    cumulativeOutputTokens: 0,
    cumulativeCacheReadTokens: 0,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'idle',
    queuePosition: 0,
    turnCount: 2,
    ...overrides,
  };
}

function input(
  sessions: OrchestratorContextSession[],
  overrides: Partial<ChatSessionListInput> = {},
): ChatSessionListInput {
  return {
    sessions,
    pendingContextKeys: new Set(),
    unreadContextKeys: new Set(),
    query: '',
    scope: 'all',
    now: NOW,
    taskTitle: () => null,
    workbenchTitle: () => null,
    modelName: () => null,
    ...overrides,
  };
}

const keys = (rows: readonly { contextKey: string }[]) => rows.map(row => row.contextKey);

describe('buildChatSessionGroups', () => {
  it('never lists a session without persisted turns, such as a seeded project chat', () => {
    const groups = buildChatSessionGroups(input([
      session({ contextKey: 'project:Fresh', projectId: 'Fresh', summary: 'Project chat for Fresh', turnCount: 0 }),
      session({ contextKey: 'project:Used', projectId: 'Used', turnCount: 3 }),
    ]));

    expect(keys(groups.recent)).toEqual(['project:Used']);
    expect(groups.active).toEqual([]);
    expect(groups.archived).toEqual([]);
  });

  it('keeps a first chat visible while its first turn is still waiting', () => {
    const groups = buildChatSessionGroups(input(
      [session({ contextKey: 'project:Fresh', turnCount: 0 })],
      { pendingContextKeys: new Set(['project:Fresh']) },
    ));

    expect(keys(groups.active)).toEqual(['project:Fresh']);
    expect(groups.active[0].liveLabel).toBe('waiting');
  });

  it('groups live sessions as Active and orders every group by last activity', () => {
    const groups = buildChatSessionGroups(input([
      session({ contextKey: 'project:Old', updatedAt: '2026-10-01T10:00:00Z' }),
      session({ contextKey: 'project:New', updatedAt: '2026-10-10T11:58:00Z' }),
      session({ contextKey: 'project:Mid', updatedAt: '2026-10-05T10:00:00Z', lastUsedAt: '2026-10-09T10:00:00Z' }),
      session({ contextKey: 'task:Agent Studio/AGT-1', kind: 'task', taskKey: 'AGT-1', runtimeStatus: 'active' }),
      session({ contextKey: 'task:Agent Studio/AGT-2', kind: 'task', taskKey: 'AGT-2', runtimeStatus: 'queued', queuePosition: 2 }),
      session({ contextKey: 'task:Agent Studio/AGT-3', kind: 'task', taskKey: 'AGT-3', runtimeStatus: 'parked' }),
    ]));

    expect(keys(groups.recent)).toEqual(['project:New', 'project:Mid', 'project:Old']);
    expect(groups.active.map(row => row.liveLabel).sort()).toEqual(['parked', 'queued 2', 'running']);
    expect(groups.active.every(row => row.live !== null)).toBe(true);
  });

  it('moves a session out of Active as soon as it is idle again', () => {
    const running = session({ contextKey: 'project:A', runtimeStatus: 'active' });
    expect(keys(buildChatSessionGroups(input([running])).active)).toEqual(['project:A']);

    const idle = { ...running, runtimeStatus: 'idle' as const };
    const groups = buildChatSessionGroups(input([idle]));
    expect(groups.active).toEqual([]);
    expect(keys(groups.recent)).toEqual(['project:A']);
  });

  it('puts operator-archived and task-archived sessions in Archived, but never a live one', () => {
    const groups = buildChatSessionGroups(input([
      session({ contextKey: 'project:A', archivedAt: '2026-10-09T10:00:00Z' }),
      session({ contextKey: 'task:Agent Studio/AGT-9', kind: 'task', taskKey: 'AGT-9', hiddenAt: '2026-10-08T10:00:00Z' }),
      session({ contextKey: 'project:B', archivedAt: '2026-10-09T10:00:00Z', runtimeStatus: 'active' }),
    ]));

    expect(keys(groups.archived).sort()).toEqual(['project:A', 'task:Agent Studio/AGT-9']);
    expect(keys(groups.active)).toEqual(['project:B']);
    expect(groups.archived.every(row => row.archived)).toBe(true);
  });

  it('does not offer archive while a turn is running, queued, or waiting', () => {
    const groups = buildChatSessionGroups(input(
      [
        session({ contextKey: 'project:Run', runtimeStatus: 'active' }),
        session({ contextKey: 'project:Queue', runtimeStatus: 'queued', queuePosition: 1 }),
        session({ contextKey: 'project:Wait' }),
        session({ contextKey: 'project:Park', runtimeStatus: 'parked' }),
        session({ contextKey: 'project:Idle' }),
      ],
      { pendingContextKeys: new Set(['project:Wait']) },
    ));
    const all = [...groups.active, ...groups.recent];
    const archivable = all.filter(row => row.canArchive).map(row => row.contextKey).sort();

    expect(archivable).toEqual(['project:Idle', 'project:Park']);
  });

  it('builds the two fixed row lines: title and time, then scope, model and tokens', () => {
    const [row] = buildChatSessionGroups(input(
      [session({
        contextKey: 'task:Agent Studio/AGT-7',
        kind: 'task',
        taskKey: 'AGT-7',
        summary: '',
        model: 'claude-opus-5-5',
        updatedAt: '2026-10-10T11:57:00Z',
        cumulativeInputTokens: 5_000_000,
        cumulativeOutputTokens: 400_000,
        cumulativeCacheReadTokens: 150_000,
        cumulativeCacheCreationTokens: 50_000,
      })],
      {
        unreadContextKeys: new Set(['task:Agent Studio/AGT-7']),
        taskTitle: (project, key) => project === 'Agent Studio' && key === 'AGT-7' ? 'Fix the release gate' : null,
      },
    )).recent;

    expect(row.title).toBe('Fix the release gate');
    expect(row.timeLabel).toBe('3m');
    expect(row.scopeLabel).toBe('AGT-7');
    expect(row.modelLabel).toBe('Opus 5.5');
    expect(row.tokenLabel).toBe('5.6m tok');
    expect(row.unread).toBe(true);
  });

  it('prefers the catalogue display name and falls back to project and Dossier names', () => {
    const groups = buildChatSessionGroups(input(
      [
        session({ contextKey: 'project:Agent Studio', summary: null, model: 'gpt-5.4' }),
        session({ contextKey: 'workbench:Agent Studio/AGT-W4', kind: 'workbench', workbenchKey: 'AGT-W4', summary: null }),
      ],
      {
        modelName: model => model === 'gpt-5.4' ? 'GPT-5.4 (catalogue)' : null,
        workbenchTitle: key => key === 'workbench:Agent Studio/AGT-W4' ? 'Release dossier' : null,
      },
    ));
    const byKey = new Map(groups.recent.map(row => [row.contextKey, row]));

    expect(byKey.get('project:Agent Studio')?.title).toBe('Agent Studio');
    expect(byKey.get('project:Agent Studio')?.modelLabel).toBe('GPT-5.4 (catalogue)');
    expect(byKey.get('workbench:Agent Studio/AGT-W4')?.title).toBe('Release dossier');
    expect(byKey.get('workbench:Agent Studio/AGT-W4')?.scopeLabel).toBe('AGT-W4');
    expect(byKey.get('workbench:Agent Studio/AGT-W4')?.tokenLabel).toBeNull();
  });

  it('filters by scope and by a case-insensitive search across title and scope', () => {
    const sessions = [
      session({ contextKey: 'project:Agent Studio', summary: 'Roadmap review' }),
      session({ contextKey: 'task:Agent Studio/AGT-5', kind: 'task', taskKey: 'AGT-5', summary: 'Flaky gate' }),
      session({ contextKey: 'workbench:Agent Studio/AGT-W1', kind: 'workbench', workbenchKey: 'AGT-W1', summary: 'Dossier talk' }),
    ];

    expect(keys(buildChatSessionGroups(input(sessions, { scope: 'tasks' })).recent)).toEqual(['task:Agent Studio/AGT-5']);
    expect(keys(buildChatSessionGroups(input(sessions, { scope: 'dossiers' })).recent)).toEqual(['workbench:Agent Studio/AGT-W1']);
    expect(keys(buildChatSessionGroups(input(sessions, { scope: 'projects' })).recent)).toEqual(['project:Agent Studio']);
    expect(keys(buildChatSessionGroups(input(sessions, { query: 'agt-5' })).recent)).toEqual(['task:Agent Studio/AGT-5']);
    expect(keys(buildChatSessionGroups(input(sessions, { query: 'ROADMAP' })).recent)).toEqual(['project:Agent Studio']);
  });
});

describe('chat session row formatting', () => {
  it('renders compact relative ages for the fixed time column', () => {
    const at = (minutesAgo: number) => new Date(NOW - minutesAgo * 60_000).toISOString();

    expect(relativeTimeLabel(at(0), NOW)).toBe('now');
    expect(relativeTimeLabel(at(3), NOW)).toBe('3m');
    expect(relativeTimeLabel(at(5 * 60), NOW)).toBe('5h');
    expect(relativeTimeLabel(at(4 * 24 * 60), NOW)).toBe('4d');
    expect(relativeTimeLabel(at(15 * 24 * 60), NOW)).toBe('2w');
    expect(relativeTimeLabel(at(90 * 24 * 60), NOW)).toBe('3mo');
    expect(relativeTimeLabel('not a date', NOW)).toBe('');
  });

  it('turns raw model ids into short display names', () => {
    expect(modelDisplayName('claude-opus-5-5')).toBe('Opus 5.5');
    expect(modelDisplayName('claude-sonnet-5')).toBe('Sonnet 5');
    expect(modelDisplayName('gpt-5.4')).toBe('GPT-5.4');
  });
});
