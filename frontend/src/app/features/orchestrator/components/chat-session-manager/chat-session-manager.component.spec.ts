import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ensureBrowserStorage } from '../../../../../testing/browser-storage';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import { OrchestratorChatSessionsStore } from '../../state/orchestrator-chat-sessions.store';
import { ChatSessionManagerComponent } from './chat-session-manager.component';

ensureBrowserStorage();

const LIST = '/api/v1/studio/orchestrator/sessions';

function session(contextKey: string, overrides: Partial<OrchestratorContextSession> = {}): OrchestratorContextSession {
  return {
    contextKey,
    kind: 'project',
    projectId: contextKey.slice('project:'.length),
    taskKey: null,
    updatedAt: new Date(Date.now() - 4 * 60_000).toISOString(),
    model: 'claude-opus-5-5',
    cumulativeInputTokens: 12_000,
    cumulativeOutputTokens: 3_000,
    cumulativeCacheReadTokens: 0,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'idle',
    queuePosition: 0,
    turnCount: 2,
    summary: `Talk about ${contextKey}`,
    ...overrides,
  };
}

describe('ChatSessionManagerComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    localStorage.removeItem('atp.chatSwitcher.seen.v1');
    await TestBed.configureTestingModule({
      imports: [ChatSessionManagerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => TestBed.resetTestingModule());

  async function render(sessions: OrchestratorContextSession[]) {
    const fixture = TestBed.createComponent(ChatSessionManagerComponent);
    fixture.componentRef.setInput('projects', ['Alpha', 'Beta']);
    http.expectOne(LIST).flush({ sessions });
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('shows Active, Recent and a collapsed Archived group, and no row for an unused chat', async () => {
    const { fixture, root } = await render([
      session('project:Seeded', { turnCount: 0, summary: 'Project chat for Seeded' }),
      session('project:Alpha', { runtimeStatus: 'active' }),
      session('project:Beta'),
      session('project:Gamma', { archivedAt: '2026-10-01T10:00:00Z' }),
    ]);

    expect(root.querySelector('[data-testid="chat-row-project:Seeded"]')).toBeNull();
    const active = root.querySelector('[data-testid="chat-group-active"]')!;
    expect(active.querySelector('[data-testid="chat-row-project:Alpha"]')).not.toBeNull();
    expect(active.querySelector('[data-testid="chat-row-state"]')?.textContent).toContain('running');
    expect(active.querySelector('[data-testid="chat-row-live-dot"]')).not.toBeNull();
    const recent = root.querySelector('[data-testid="chat-group-recent"]')!;
    expect(recent.querySelector('[data-testid="chat-row-model"]')?.textContent).toBe('Opus 5.5');
    expect(recent.querySelector('[data-testid="chat-row-tokens"]')?.textContent).toBe('15k tok');
    expect(recent.querySelector('[data-testid="chat-row-time"]')?.textContent).toBe('4m');
    expect(root.querySelector('[data-testid="chat-row-project:Gamma"]')).toBeNull();

    (root.querySelector('[data-testid="chat-group-archived-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="chat-row-project:Gamma"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="chat-row-unarchive-project:Gamma"]')).not.toBeNull();
  });

  it('archives an idle row but offers no archive on a running one', async () => {
    const { fixture, root } = await render([
      session('project:Alpha', { runtimeStatus: 'active' }),
      session('project:Beta'),
    ]);

    expect(root.querySelector('[data-testid="chat-row-archive-project:Alpha"]')).toBeNull();
    (root.querySelector('[data-testid="chat-row-archive-project:Beta"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="chat-group-recent"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-group-archived"]')).not.toBeNull();
    const request = http.expectOne('/api/v1/studio/orchestrator/sessions/project:Beta/archive');
    expect(request.request.body).toEqual({ archived: true });
  });

  it('opens a row in the side sheet, marks it read, and keeps navigate as a secondary action', async () => {
    const { fixture, root } = await render([session('project:Beta')]);
    const opened: string[] = [];
    const located: string[] = [];
    fixture.componentInstance.sessionOpened.subscribe(key => opened.push(key));
    fixture.componentInstance.locationRequested.subscribe(key => located.push(key));
    expect(root.querySelector('[data-testid="chat-row-unread"]')).not.toBeNull();

    (root.querySelector('[data-testid="chat-row-open-project:Beta"]') as HTMLButtonElement).click();
    (root.querySelector('[data-testid="chat-row-navigate-project:Beta"]') as HTMLButtonElement).click();
    TestBed.inject(OrchestratorChatSessionsStore).setOpenContext('project:Beta');
    fixture.detectChanges();

    expect(opened).toEqual(['project:Beta']);
    expect(located).toEqual(['project:Beta']);
    expect(root.querySelector('[data-testid="chat-row-unread"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-row-project:Beta"]')?.classList).toContain('chat-row--open');
  });

  it('starts a new chat in the current task, globally, or in a chosen project', async () => {
    const { fixture, root } = await render([]);
    TestBed.inject(OrchestratorChatSessionsStore).setCurrentScope({
      contextKey: 'task:Alpha/ALP-3', kind: 'task', label: 'ALP-3',
    });
    const requested: (string | null)[] = [];
    fixture.componentInstance.newChatRequested.subscribe(key => requested.push(key));
    expect(root.querySelector('[data-testid="chat-empty"]')).not.toBeNull();

    const items = fixture.componentInstance.newChatItems();
    expect(items.filter(item => item.kind === 'row').map(item => item.kind === 'row' ? item.label : ''))
      .toEqual(['Current task: ALP-3', 'Global', 'Alpha', 'Beta']);
    for (const id of ['current', 'global', 'project:Beta']) {
      const item = items.find(entry => entry.kind === 'row' && entry.id === id);
      fixture.componentInstance.onNewChat({ id, item: item as never });
    }

    expect(requested).toEqual([null, 'global', 'project:Beta']);
  });

  it('filters rows by search text and scope', async () => {
    const { fixture, root } = await render([
      session('project:Alpha'),
      session('task:Alpha/ALP-9', { kind: 'task', projectId: 'Alpha', taskKey: 'ALP-9', summary: 'Gate retry' }),
    ]);

    fixture.componentInstance.scope.set('tasks');
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="chat-row-project:Alpha"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-row-task:Alpha/ALP-9"]')).not.toBeNull();

    fixture.componentInstance.scope.set('all');
    fixture.componentInstance.query.set('nothing matches');
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="chat-empty-filtered"]')).not.toBeNull();
  });
});
