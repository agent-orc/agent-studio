import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ensureBrowserStorage } from '../../../../testing/browser-storage';
import type { OrchestratorContextSession } from '../models/orchestrator.model';
import { OrchestratorChatSessionsStore } from './orchestrator-chat-sessions.store';

ensureBrowserStorage();

const LIST = '/api/v1/studio/orchestrator/sessions';

function session(contextKey: string, overrides: Partial<OrchestratorContextSession> = {}): OrchestratorContextSession {
  return {
    contextKey,
    kind: contextKey.startsWith('task:') ? 'task' : 'project',
    projectId: 'Agent Studio',
    taskKey: contextKey.startsWith('task:') ? contextKey.split('/')[1] : null,
    updatedAt: '2026-10-10T10:00:00Z',
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
  let store: OrchestratorChatSessionsStore;
  let http: HttpTestingController;
  let release: () => void;

  beforeEach(() => {
    localStorage.removeItem('atp.chatSwitcher.seen.v1');
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    store = TestBed.inject(OrchestratorChatSessionsStore);
    http = TestBed.inject(HttpTestingController);
    release = store.connect();
    http.expectOne(LIST).flush({
      sessions: [session('project:Agent Studio'), session('task:Agent Studio/AGT-7')],
    });
  });

  afterEach(() => {
    release();
    http.verify();
    TestBed.resetTestingModule();
  });

  it('loads the workspace session list once for its first consumer', () => {
    const second = store.connect();

    expect(store.loaded()).toBe(true);
    expect(store.sessions().map(item => item.contextKey)).toEqual(['project:Agent Studio', 'task:Agent Studio/AGT-7']);
    second();
  });

  it('archives through the per-shape Studio route and moves the row at once', () => {
    store.setArchived('task:Agent Studio/AGT-7', true);

    expect(store.sessions().find(item => item.contextKey === 'task:Agent Studio/AGT-7')?.archivedAt).toBeTruthy();
    const request = http.expectOne('/api/v1/studio/orchestrator/sessions/task:Agent%20Studio/AGT-7/archive');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ archived: true });
    request.flush({});
    http.expectOne(LIST).flush({
      sessions: [session('task:Agent Studio/AGT-7', { archivedAt: '2026-10-10T11:00:00Z' })],
    });
    expect(store.sessions()[0].archivedAt).toBe('2026-10-10T11:00:00Z');
  });

  it('keeps an in-flight archive mark against a poll answered before the write', () => {
    store.setArchived('project:Agent Studio', true);
    store.refresh();
    http.expectOne(LIST).flush({ sessions: [session('project:Agent Studio', { archivedAt: null })] });

    expect(store.sessions()[0].archivedAt).toBeTruthy();
    http.expectOne(request => request.url.endsWith('/archive')).flush({});
    http.expectOne(LIST).flush({ sessions: [session('project:Agent Studio', { archivedAt: '2026-10-10T11:00:00Z' })] });
  });

  it('restores the previous mark and reports the failure when unarchive fails', () => {
    store.refresh();
    http.expectOne(LIST).flush({
      sessions: [session('project:Agent Studio', { archivedAt: '2026-10-09T09:00:00Z' })],
    });

    store.setArchived('project:Agent Studio', false);
    expect(store.sessions()[0].archivedAt).toBeNull();
    http.expectOne('/api/v1/studio/orchestrator/sessions/project:Agent%20Studio/archive')
      .flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });

    expect(store.sessions()[0].archivedAt).toBe('2026-10-09T09:00:00Z');
    expect(store.error()).toBe('The chat could not be restored.');
  });

  it('marks a session unread until it is opened, and never the open one', () => {
    store.markSeen('project:Agent Studio');
    store.refresh();
    http.expectOne(LIST).flush({
      sessions: [
        session('project:Agent Studio', { updatedAt: '2026-10-10T12:00:00Z' }),
        session('task:Agent Studio/AGT-7', { updatedAt: '2026-10-10T12:00:00Z' }),
      ],
    });
    expect([...store.unreadContextKeys()].sort()).toEqual(['project:Agent Studio', 'task:Agent Studio/AGT-7']);

    store.setOpenContext('task:Agent Studio/AGT-7');

    expect([...store.unreadContextKeys()]).toEqual(['project:Agent Studio']);
  });
});
