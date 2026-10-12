import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';
import { TaskService } from '../../../services/task.service';
import type { OrchestratorContextSession, OrchestratorContextSessionsResponse } from '../models/orchestrator.model';
import { OrchestratorChatSessionsStore } from './orchestrator-chat-sessions.store';
import { ensureBrowserStorage } from '../../../../testing/browser-storage';

ensureBrowserStorage();

function session(contextKey: string, overrides: Partial<OrchestratorContextSession> = {}): OrchestratorContextSession {
  return {
    contextKey,
    kind: 'project',
    projectId: contextKey.slice('project:'.length),
    taskKey: null,
    updatedAt: '2026-10-11T10:00:00Z',
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

describe('OrchestratorChatSessionsStore', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem('atp.chatSwitcher.seen.v1');
    vi.useRealTimers();
  });

  function setup() {
    const responses: Subject<OrchestratorContextSessionsResponse>[] = [];
    const archive = new Subject<OrchestratorContextSession>();
    const tasks = {
      getOrchestratorContextSessions: vi.fn(() => {
        const response = new Subject<OrchestratorContextSessionsResponse>();
        responses.push(response);
        return response;
      }),
      setOrchestratorContextArchived: vi.fn(() => archive),
    };
    const revision = signal(0);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: tasks },
        { provide: JobsHubClient, useValue: { orchestratorContextsRevision: revision } },
      ],
    });
    const store = TestBed.inject(OrchestratorChatSessionsStore);
    return { store, tasks, responses, archive, revision };
  }

  it('loads once on attach, drops overlapping refreshes, and polls only while attached', () => {
    vi.useFakeTimers();
    const { store, tasks, responses } = setup();

    const detach = store.attach();
    TestBed.tick();
    store.refresh();
    expect(tasks.getOrchestratorContextSessions).toHaveBeenCalledTimes(1);
    responses[0].next({ sessions: [session('project:A')] });
    expect(store.sessions().map(item => item.contextKey)).toEqual(['project:A']);

    vi.advanceTimersByTime(2_000);
    expect(tasks.getOrchestratorContextSessions).toHaveBeenCalledTimes(2);
    responses[1].next({ sessions: [] });

    detach();
    vi.advanceTimersByTime(10_000);
    expect(tasks.getOrchestratorContextSessions).toHaveBeenCalledTimes(2);
  });

  it('refreshes on a live context change from SignalR while attached', () => {
    const { store, tasks, responses, revision } = setup();
    store.attach();
    TestBed.tick();
    responses[0].next({ sessions: [] });
    expect(tasks.getOrchestratorContextSessions).toHaveBeenCalledTimes(1);

    revision.set(1);
    TestBed.tick();

    expect(tasks.getOrchestratorContextSessions).toHaveBeenCalledTimes(2);
  });

  it('moves a session to archived immediately and reverts on failure', () => {
    const { store, tasks, archive } = setup();
    store.sessions.set([session('project:A')]);

    store.setArchived('project:A', true);
    expect(tasks.setOrchestratorContextArchived).toHaveBeenCalledWith('project:A', true);
    expect(store.sessions()[0].archivedAt).toBeTruthy();

    archive.error(new Error('down'));
    expect(store.sessions()[0].archivedAt).toBeNull();
    expect(store.archiveError()).toContain('could not be archived');
  });

  it('marks the open session as read and everything newer elsewhere as unread', () => {
    const { store } = setup();
    store.sessions.set([
      session('project:A', { updatedAt: '2026-10-11T10:00:00Z' }),
      session('project:B', { updatedAt: '2026-10-11T11:00:00Z' }),
    ]);
    store.markSeen('project:B');
    expect([...store.unreadContextKeys()]).toEqual(['project:A']);

    store.openContextKey.set('project:A');
    expect(store.unreadContextKeys().size).toBe(0);
  });

  it('treats first-seen sessions as read and flags only later activity', () => {
    const { store, responses } = setup();
    store.attach();
    responses[0].next({ sessions: [session('project:A', { updatedAt: '2026-10-11T10:00:00Z' })] });
    expect(store.unreadContextKeys().size).toBe(0);

    store.refresh();
    responses[1].next({ sessions: [session('project:A', { updatedAt: '2026-10-11T10:05:00Z' })] });
    expect([...store.unreadContextKeys()]).toEqual(['project:A']);
  });

  it('relays panel requests to the side sheet', () => {
    const { store } = setup();
    const seen: string[] = [];
    store.requests.subscribe(request => seen.push(`${request.kind}:${request.contextKey}`));

    store.requestOpen('project:A');
    store.requestNavigate('project:A');

    expect(seen).toEqual(['open:project:A', 'navigate:project:A']);
  });
});
