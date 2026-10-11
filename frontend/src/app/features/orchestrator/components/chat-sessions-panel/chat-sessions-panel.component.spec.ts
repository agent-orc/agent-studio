import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { TaskService } from '../../../../services/task.service';
import { CliCatalogStore } from '../../../cli/catalog';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import { OrchestratorChatActivityService } from '../../state/orchestrator-chat-activity.service';
import { OrchestratorChatSessionsStore } from '../../state/orchestrator-chat-sessions.store';
import { ChatSessionsPanelComponent } from './chat-sessions-panel.component';

function context(overrides: Partial<OrchestratorContextSession> & Pick<OrchestratorContextSession, 'contextKey'>): OrchestratorContextSession {
  return {
    kind: overrides.contextKey.startsWith('task:') ? 'task' : 'project',
    projectId: 'Agent Studio',
    taskKey: null,
    updatedAt: new Date(Date.now() - 5 * 60_000).toISOString(),
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

class TaskServiceStub {
  sessions: OrchestratorContextSession[] = [];
  readonly jobs = signal([{ projectName: 'Agent Studio', taskKey: 'AGT-3032', displayKey: 'AGT-3032', key: 'AGT-3032', title: 'Chat sessions panel' }]);
  readonly archiveCalls: { contextKey: string; archived: boolean }[] = [];
  failArchive = false;
  pendingArchive: Subject<OrchestratorContextSession> | null = null;

  getOrchestratorContextSessions() {
    return of({ sessions: this.sessions });
  }

  setOrchestratorContextArchived(contextKey: string, archived: boolean) {
    this.archiveCalls.push({ contextKey, archived });
    if (this.failArchive) return throwError(() => new Error('nope'));
    if (this.pendingArchive) return this.pendingArchive;
    this.sessions = this.sessions.map(session => session.contextKey === contextKey
      ? { ...session, archivedAt: archived ? '2026-10-11T12:00:00Z' : null }
      : session);
    return of(this.sessions.find(session => session.contextKey === contextKey)!);
  }
}

class CatalogStub {
  readonly catalogs = signal(new Map([['codex', {
    models: [{ id: 'gpt-5.5', label: 'GPT-5.5', multiplier: null, vendor: null, isDefault: true }],
    source: 'test',
    fetchedAt: 0,
  }]]));
  hydrateAll(): void { /* catalogue already loaded */ }
}

describe('ChatSessionsPanelComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  function mount(sessions: OrchestratorContextSession[], projects: string[] = ['Agent Studio']) {
    const tasks = new TaskServiceStub();
    tasks.sessions = sessions;
    TestBed.configureTestingModule({
      imports: [ChatSessionsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: tasks },
        { provide: JobsHubClient, useValue: { orchestratorContextsRevision: signal(0), connected: signal(true) } },
        { provide: CliCatalogStore, useValue: new CatalogStub() },
      ],
    });
    const fixture = TestBed.createComponent(ChatSessionsPanelComponent);
    fixture.componentRef.setInput('projects', projects);
    fixture.detectChanges();
    const store = TestBed.inject(OrchestratorChatSessionsStore);
    return { fixture, tasks, store, host: fixture.nativeElement as HTMLElement };
  }

  it('lists only sessions with turns and renders a two-line row without pills', () => {
    const { host } = mount([
      context({ contextKey: 'project:Fresh', projectId: 'Fresh', turnCount: 0, summary: 'Project chat for Fresh' }),
      context({
        contextKey: 'task:Agent Studio/AGT-3032',
        taskKey: 'AGT-3032',
        summary: 'Move chats to the sidebar',
        model: 'gpt-5.5',
        cumulativeInputTokens: 5_400_000,
        cumulativeOutputTokens: 200_000,
      }),
    ]);

    expect(host.textContent).not.toContain('Project chat for Fresh');
    const row = host.querySelector('[data-testid="chat-session-row-task:Agent Studio/AGT-3032"]')!;
    expect(row.querySelector('[data-testid="chat-session-title"]')?.textContent?.trim()).toBe('Move chats to the sidebar');
    expect(row.querySelector('[data-testid="chat-session-time"]')?.textContent?.trim()).toBe('5m');
    expect(row.querySelector('[data-testid="chat-session-scope"]')?.textContent?.trim()).toBe('AGT-3032');
    expect(row.querySelector('[data-testid="chat-session-model"]')?.textContent?.trim()).toBe('GPT-5.5');
    expect(row.querySelector('[data-testid="chat-session-tokens"]')?.textContent?.trim()).toBe('5.6m tok');
    expect(host.textContent).not.toMatch(/\bnew\b/);
  });

  it('shows working sessions in Active with a live state word', () => {
    const { host, fixture } = mount([
      context({ contextKey: 'project:Agent Studio', runtimeStatus: 'active' }),
      context({ contextKey: 'task:Agent Studio/AGT-1', taskKey: 'AGT-1', runtimeStatus: 'queued', queuePosition: 2 }),
    ]);
    TestBed.inject(OrchestratorChatActivityService).start('task:Agent Studio/AGT-1');
    fixture.detectChanges();

    const active = host.querySelector('[data-testid="chat-sessions-active"]')!;
    const words = [...active.querySelectorAll('[data-testid="chat-session-live"]')].map(item => item.textContent?.trim());
    expect(words.sort()).toEqual(['queued 2', 'running']);
    expect(host.querySelector('[data-testid="chat-sessions-active-head"]')?.textContent).toContain('2');
    expect(active.querySelector('[data-testid^="chat-session-archive-"]')).toBeNull();
  });

  it('archives a row into the collapsed Archived group and restores it', () => {
    const { host, fixture, tasks } = mount([
      context({ contextKey: 'project:Agent Studio', summary: 'Release planning' }),
    ]);

    host.querySelector<HTMLButtonElement>('[data-testid="chat-session-archive-project:Agent Studio"]')!.click();
    fixture.detectChanges();

    expect(tasks.archiveCalls).toEqual([{ contextKey: 'project:Agent Studio', archived: true }]);
    expect(host.querySelector('[data-testid="chat-sessions-recent"]')).toBeNull();
    const toggle = host.querySelector<HTMLButtonElement>('[data-testid="chat-sessions-archived-toggle"]')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.textContent).toContain('1');

    toggle.click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('[data-testid="chat-session-unarchive-project:Agent Studio"]')!.click();
    fixture.detectChanges();

    expect(tasks.archiveCalls.at(-1)).toEqual({ contextKey: 'project:Agent Studio', archived: false });
    expect(host.querySelector('[data-testid="chat-session-row-project:Agent Studio"]')?.closest('[data-testid="chat-sessions-recent"]')).not.toBeNull();
  });

  it('restores the row and reports when the archive request fails', () => {
    const { host, fixture, tasks } = mount([context({ contextKey: 'project:Agent Studio' })]);
    tasks.failArchive = true;

    host.querySelector<HTMLButtonElement>('[data-testid="chat-session-archive-project:Agent Studio"]')!.click();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="chat-sessions-recent"] [data-testid="chat-session-row-project:Agent Studio"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="chat-sessions-archive-error"]')?.textContent).toContain('could not be archived');
  });

  it('opens a row in the side sheet and highlights the open session', () => {
    const { host, fixture, store } = mount([context({ contextKey: 'project:Agent Studio' })]);
    const requests: string[] = [];
    store.requests.subscribe(request => requests.push(`${request.kind}:${request.contextKey}`));

    host.querySelector<HTMLButtonElement>('[data-testid="chat-session-row-project:Agent Studio"] [data-testid="chat-session-open"]')!.click();
    host.querySelector<HTMLButtonElement>('[data-testid="chat-session-navigate-project:Agent Studio"]')!.click();
    store.openContextKey.set('project:Agent Studio');
    fixture.detectChanges();

    expect(requests).toEqual(['open:project:Agent Studio', 'navigate:project:Agent Studio']);
    const open = host.querySelector('[data-testid="chat-session-row-project:Agent Studio"] [data-testid="chat-session-open"]');
    expect(open?.getAttribute('aria-current')).toBe('true');
  });

  it('starts a new chat in the current task or a project scope', () => {
    const { host, fixture, store } = mount([], ['Agent Studio', 'Quality Studio']);
    store.navigationContextKey.set('task:Agent Studio/AGT-3032');
    const requests: string[] = [];
    store.requests.subscribe(request => requests.push(request.contextKey));

    host.querySelector<HTMLButtonElement>('[data-testid="chat-sessions-new"]')!.click();
    fixture.detectChanges();
    const items = fixture.componentInstance.newChatItems()
      .filter(item => item.kind === 'row')
      .map(item => item.id);
    expect(items).toEqual(['task:Agent Studio/AGT-3032', 'project:Agent Studio', 'project:Quality Studio']);

    fixture.componentInstance.onNewChat({ id: 'project:Quality Studio', item: { kind: 'row', id: 'project:Quality Studio', label: 'Quality Studio' } });
    expect(requests).toEqual(['project:Quality Studio']);
    expect(host.querySelector('[data-testid="chat-sessions-empty"]')?.textContent).toContain('No chats yet');
  });
});
