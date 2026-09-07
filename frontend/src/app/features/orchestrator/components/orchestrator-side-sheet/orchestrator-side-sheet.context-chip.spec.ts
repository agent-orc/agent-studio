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

  it('renders CAC context chips above a compact footer without toolbar, breadcrumb, or image upload', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('composerContext', { project: 'demo-project', surface: 'Board' });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelectorAll('[data-testid="chat-composer-foot"]')).toHaveLength(1);
    expect(root.querySelector('[data-testid="chat-toolbar"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-composer-context-project"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-attach"]')).toBeNull();
    expect(root.querySelector('input[type="file"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-context-attachments"]')?.textContent)
      .toContain('Board · ~1.6k');
    expect(root.textContent).not.toContain('Make a task from your message');
    expect(root.textContent).not.toContain('Make a task from this reply');
    expect(root.querySelector('[data-testid="orch-side-sheet-draft-actions"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-toolbar-task"]')).toBeNull();
  });

  it('adds context through the CAC chip row, updates the estimate, and removes the attachment', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('composerContext', { project: 'demo-project', surface: 'Board' });
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const root = fixture.nativeElement as HTMLElement;
    const source = {
      id: 'repository-file:demo-project:docs/context.md',
      category: 'files' as const,
      label: 'docs/context.md',
      detail: 'Repository file',
      estimateTokens: 700,
      reference: {
        kind: 'repository-file' as const,
        reference: 'docs/context.md',
        projectId: 'demo-project',
      },
    };

    expect(root.querySelector('[data-testid="chat-context-attachments"]')?.textContent)
      .toContain('Board · ~1.6k');
    root.querySelector<HTMLButtonElement>('[data-testid="chat-context-attachment-add"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="orch-context-source-picker"]')).not.toBeNull();

    component.addContextAttachment(source);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="chat-context-attachments"]')?.textContent)
      .toContain('Board · ~2.3k');
    expect(root.querySelector('[data-testid="chat-context-attachments"]')?.textContent)
      .toContain('docs/context.md');

    root.querySelector<HTMLButtonElement>(
      `[data-testid="chat-context-attachment-remove-${source.id}"]`,
    )!.click();
    fixture.detectChanges();
    expect(component.contextAttachments()).toEqual([]);
    expect(root.querySelector('[data-testid="chat-context-attachments"]')?.textContent)
      .toContain('Board · ~1.6k');
  });

  it('forwards live active-tab context without remounting CAC or losing its draft', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('projects', ['Agent Studio']);
    fixture.componentInstance.activeProject.set('Agent Studio');
    fixture.componentRef.setInput('composerContext', { project: 'Agent Studio', surface: 'Board' });
    fixture.detectChanges();
    const firstChat = fixture.debugElement.query(By.directive(ChatComponent)).componentInstance as ChatComponent;
    const textarea = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLTextAreaElement>('[data-testid="chat-input"]')!;
    textarea.value = 'Draft survives navigation';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    fixture.componentRef.setInput('composerContext', {
      project: 'Agent Studio',
      surface: 'Task',
      detail: 'AGT-2162',
    });
    fixture.detectChanges();

    const secondChat = fixture.debugElement.query(By.directive(ChatComponent)).componentInstance as ChatComponent;
    expect(secondChat).toBe(firstChat);
    expect(textarea.value).toBe('Draft survives navigation');
    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="chat-context-attachments"]')?.textContent).toContain('AGT-2162 · ~1.6k');
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
