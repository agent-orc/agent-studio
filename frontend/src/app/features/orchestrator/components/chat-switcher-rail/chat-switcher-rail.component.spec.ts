import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChatSwitcherRailComponent } from './chat-switcher-rail.component';

describe('ChatSwitcherRailComponent', () => {
  let fixture: ComponentFixture<ChatSwitcherRailComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ChatSwitcherRailComponent] }).compileComponents();
    fixture = TestBed.createComponent(ChatSwitcherRailComponent);
    fixture.componentRef.setInput('projects', ['Alpha']);
    fixture.componentRef.setInput('activeContextKey', 'task:Alpha/A-1');
    fixture.componentRef.setInput('unreadContextKeys', new Set(['task:Alpha/A-1']));
    fixture.componentRef.setInput('sessions', [{
      contextKey: 'task:Alpha/A-1', kind: 'task', projectId: 'Alpha', taskKey: 'A-1',
      updatedAt: '2026-07-11T10:00:00Z', model: 'codex', cumulativeInputTokens: 1200,
      cumulativeOutputTokens: 50, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'parked', queuePosition: 0,
      summary: 'Investigate the task context lifecycle',
    }]);
    fixture.detectChanges();
  });

  it('always renders the grouped context list with the current row state', () => {
    expect(fixture.nativeElement.querySelector('[data-testid="chat-switcher-chip"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="chat-context-list"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="chat-context-groups"]')).not.toBeNull();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Global');
    expect(text).toContain('Projects');
    expect(text).toContain('Tasks');
    expect(text).toContain('parked');
    expect(text).toContain('new');
    expect(text).toContain('1k');
    expect(text).toContain('Investigate the task context lifecycle');

    const current = fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-task:Alpha/A-1"]');
    expect(current.classList).toContain('context-list__row--active');
  });

  it('keeps chat selection separate from location navigation', () => {
    const selected = vi.fn();
    const navigated = vi.fn();
    fixture.componentInstance.contextSelected.subscribe(selected);
    fixture.componentInstance.locationRequested.subscribe(navigated);

    const row = fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-task:Alpha/A-1"]');
    row.querySelector('.context-list__name').click();
    fixture.nativeElement.querySelector('[data-testid="chat-switcher-navigate-task:Alpha/A-1"]').click();

    expect(selected).toHaveBeenCalledWith('task:Alpha/A-1');
    expect(navigated).toHaveBeenCalledWith('task:Alpha/A-1');
  });

  it('renders exactly one permanent row for each project identity', () => {
    const projectSession = {
      contextKey: 'project:Alpha', kind: 'project' as const, projectId: 'Alpha', taskKey: null,
      updatedAt: '2026-08-10T10:00:00Z', model: null, cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'idle' as const, queuePosition: 0, summary: 'Permanent project conversation',
    };
    fixture.componentRef.setInput('sessions', [projectSession, { ...projectSession }]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('[data-testid="chat-switcher-row-project:Alpha"]'))
      .toHaveLength(1);
  });

  it('groups Dossier sessions under a Dossiers heading between Projects and Tasks', () => {
    const workbenchSession = {
      contextKey: 'workbench:Alpha/AGT-W43', kind: 'workbench' as const, projectId: 'Alpha', taskKey: null,
      workbenchKey: 'AGT-W43', updatedAt: '2026-08-10T10:00:00Z', model: null, cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'idle' as const, queuePosition: 0, summary: 'Dossier chat for AGT-W43',
    };
    fixture.componentRef.setInput('sessions', [
      ...fixture.componentInstance.sessions(),
      workbenchSession,
    ]);
    fixture.detectChanges();

    const groupHeadings = [...fixture.nativeElement.querySelectorAll('.context-list__group h3')]
      .map((el: HTMLElement) => el.textContent);
    expect(groupHeadings).toEqual(['Global', 'Projects', 'Dossiers', 'Tasks']);

    const row = fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-workbench:Alpha/AGT-W43"]');
    expect(row).not.toBeNull();
    expect(row.textContent).toContain('AGT-W43');
  });

  it('prefers an open Dossier tab title over the bare key in the rail label', () => {
    const workbenchSession = {
      contextKey: 'workbench:Alpha/AGT-W43', kind: 'workbench' as const, projectId: 'Alpha', taskKey: null,
      workbenchKey: 'AGT-W43', updatedAt: '2026-08-10T10:00:00Z', model: null, cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0, cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'idle' as const, queuePosition: 0,
    };
    fixture.componentRef.setInput('sessions', [
      ...fixture.componentInstance.sessions(),
      workbenchSession,
    ]);
    fixture.componentRef.setInput(
      'workbenchTitles',
      new Map([['workbench:Alpha/AGT-W43', 'Context-Aware Orchestrator Chats']]),
    );
    fixture.detectChanges();

    const row = fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-workbench:Alpha/AGT-W43"]');
    expect(row.textContent).toContain('Context-Aware Orchestrator Chats');
  });

  it('uses an acute working marker only while a reply is outstanding', () => {
    fixture.componentRef.setInput('pendingContextKeys', new Set(['task:Alpha/A-1']));
    fixture.detectChanges();
    const row = fixture.nativeElement.querySelector('[data-testid="chat-switcher-row-task:Alpha/A-1"]');

    expect(row.classList).toContain('context-list__row--working');
    expect(row.getAttribute('data-runtime-status')).toBe('pending');
    expect(row.textContent).toContain('waiting');

    fixture.componentRef.setInput('pendingContextKeys', new Set());
    fixture.detectChanges();
    expect(row.classList).not.toContain('context-list__row--working');
  });
});
