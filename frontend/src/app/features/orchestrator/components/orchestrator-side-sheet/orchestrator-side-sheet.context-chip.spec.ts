import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { ChatComponent } from 'coding-agent-chat/composer';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

describe('OrchestratorSideSheetComponent context badge and menu', () => {
  beforeEach(() => sessionStorage.removeItem('atp.studio.orchestratorOpen.v1'));

  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorSideSheetComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['demo-project']);
    fixture.componentInstance.activeProject.set('demo-project');
    return fixture;
  }

  it('counts every distinct global, project and task context', async () => {
    const fixture = await makeFixture();
    const session = (contextKey: string, kind: OrchestratorContextSession['kind']): OrchestratorContextSession => ({
      contextKey,
      kind,
      projectId: kind === 'global' ? null : 'demo-project',
      taskKey: kind === 'task' ? 'AGT-2087' : null,
      updatedAt: '2026-07-11T10:00:00Z',
      model: null,
      cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0,
      cumulativeCacheReadTokens: 0,
      cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'idle',
      queuePosition: 0,
    });

    fixture.componentInstance.contextSessions.set([
      session('project:demo-project', 'project'),
      session('task:demo-project/AGT-2087', 'task'),
    ]);

    expect(fixture.componentInstance.contextCount()).toBe(3);
  });

  it('keeps only picker and count badge in the collapsed header', async () => {
    const fixture = await makeFixture();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="orch-context-badge"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-context-count"]')?.textContent?.trim()).toBe('2');
    expect(root.querySelector('[data-testid="orch-context-menu"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-pin"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-settings"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-refresh"]')).toBeNull();
  });

  it('opens the full context menu and moves header actions into it', async () => {
    const fixture = await makeFixture();
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    (root.querySelector('[data-testid="orch-context-badge"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="orch-context-menu"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="chat-context-list"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-pin"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-settings"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-refresh"]')).not.toBeNull();
    expect(root.textContent).toContain('Chat history');
  });

  it('offers a repository page as the first stable current-tab source', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('pageContext', {
      projectName: 'demo-project', relPath: 'concepts/context.md', title: 'Context model',
      pageType: 'concept', excerpt: 'Conversation scope and source receipts.',
    });
    fixture.componentRef.setInput('composerContext', {
      project: 'demo-project', surface: 'Wiki', detail: 'Context model',
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.currentTabSource()?.reference).toEqual({
      kind: 'page', reference: 'page:demo-project/concepts/context.md', projectId: 'demo-project',
    });
    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="chat-context-attachment-add"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="orch-context-current-source"]')?.textContent)
      .toContain('Context model');
  });

  it('renders the standard composer footer once and removes both host task workflows', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('composerContext', { project: 'demo-project', surface: 'Board' });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelectorAll('[data-testid="chat-composer-foot"]')).toHaveLength(1);
    expect(root.textContent).not.toContain('Make a task from your message');
    expect(root.textContent).not.toContain('Make a task from this reply');
    expect(root.querySelector('[data-testid="orch-side-sheet-draft-actions"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-toolbar-task"]')).toBeNull();
  });

  it('forwards live active-tab context without remounting CAC or losing its draft', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('composerContext', { project: 'demo-project', surface: 'Board' });
    fixture.detectChanges();
    const firstChat = fixture.debugElement.query(By.directive(ChatComponent)).componentInstance as ChatComponent;
    const textarea = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLTextAreaElement>('[data-testid="chat-input"]')!;
    textarea.value = 'Draft survives navigation';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    fixture.componentRef.setInput('composerContext', {
      project: 'demo-project',
      surface: 'Dossier',
      detail: 'Runner link health',
    });
    fixture.detectChanges();

    const secondChat = fixture.debugElement.query(By.directive(ChatComponent)).componentInstance as ChatComponent;
    expect(secondChat).toBe(firstChat);
    expect(textarea.value).toBe('Draft survives navigation');
    // The automatic chip is now the composer's location indicator.
    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="chat-context-attachment-context:automatic"]')?.textContent)
      .toContain('Runner link health');
  });

  it('derives a workbench (Dossier) context key from a Dossier tab and reverts to project scope when it closes', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('composerContext', { project: 'demo-project', surface: 'Board' });
    fixture.detectChanges();
    expect(fixture.componentInstance.contextKind()).toBe('project');
    expect(fixture.componentInstance.contextKey()).toBe('project:demo-project');

    fixture.componentRef.setInput('composerContext', {
      project: 'demo-project',
      surface: 'Dossier',
      detail: 'Runner link health',
      referenceKey: 'AGT-W43',
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.contextKind()).toBe('workbench');
    expect(fixture.componentInstance.contextKey()).toBe('workbench:demo-project/AGT-W43');

    // Leaving the Dossier returns to the project session.
    fixture.componentRef.setInput('composerContext', { project: 'demo-project', surface: 'Board' });
    fixture.detectChanges();
    expect(fixture.componentInstance.contextKind()).toBe('project');
    expect(fixture.componentInstance.contextKey()).toBe('project:demo-project');

    // Returning to the same Dossier shows the Dossier session again.
    fixture.componentRef.setInput('composerContext', {
      project: 'demo-project',
      surface: 'Dossier',
      detail: 'Runner link health',
      referenceKey: 'AGT-W43',
    });
    fixture.detectChanges();
    expect(fixture.componentInstance.contextKey()).toBe('workbench:demo-project/AGT-W43');
  });

  it('renders the Dossier automatic context chip as mandatory (not dismissable)', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('composerContext', {
      project: 'demo-project',
      surface: 'Dossier',
      detail: 'Runner link health',
      referenceKey: 'AGT-W43',
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.automaticContextIncluded()).toBe(true);
    const chip = fixture.componentInstance.cacContextAttachments()
      .find(item => item.id === 'context:automatic');
    expect(chip?.hint).toContain('always included');

    // The dismiss affordance is a no-op in Dossier scope, same as task scope.
    fixture.componentInstance.toggleNextMessageContext();
    expect(fixture.componentInstance.automaticContextIncluded()).toBe(true);
  });

  it('shows the persisted context receipt for the latest orchestrator answer', async () => {
    const fixture = await makeFixture();
    fixture.componentInstance.turns.set([{
      id: 'answer-1',
      ts: '2026-08-08T10:00:00Z',
      role: 'orchestrator',
      text: 'status.md records the task result.',
      contextReceipt: {
        scope: 'task',
        contextKey: 'task:Quality Studio/QS-54',
        taskKey: 'QS-54',
        includedBlocks: ['task metadata', 'prompt.md', 'status.md', 'last run outcome'],
        capturedAt: '2026-08-08T09:59:58Z',
      },
    }]);

    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="orch-answer-context-receipt"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-answer-context-scope"]')?.textContent?.trim()).toBe('QS-54');
    expect(root.querySelector('[data-testid="orch-answer-context-blocks"]')?.textContent)
      .toContain('status.md');
  });
});
