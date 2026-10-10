import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { OrchestratorPanelHeaderComponent } from './orchestrator-panel-header.component';

describe('OrchestratorPanelHeaderComponent', () => {
  it('uses the task identity as the primary header context', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorPanelHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorPanelHeaderComponent);
    fixture.componentRef.setInput('project', 'Agent Studio');
    fixture.componentRef.setInput('taskKey', 'AGT-2613');
    fixture.componentRef.setInput('taskTitle', 'Repair the panel frame');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="orch-panel-context-type"]')?.textContent).toContain(
      'Task',
    );
    expect(host.querySelector('[data-testid="orch-panel-context-name"]')?.textContent).toContain(
      'AGT-2613 · Repair the panel frame',
    );
  });

  it('lets a Dossier page replace the project fallback without changing the action row', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorPanelHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorPanelHeaderComponent);
    fixture.componentRef.setInput('project', 'Agent Studio');
    fixture.componentRef.setInput('pageContext', {
      projectName: 'Agent Studio',
      relPath: 'concepts/panel-frame.html',
      title: 'AGT-W34',
      pageType: 'workbench',
      excerpt: '',
    });
    fixture.componentRef.setInput('activeChatCount', 2);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="orch-panel-context-type"]')?.textContent).toContain(
      'Dossier',
    );
    expect(host.querySelector('[data-testid="orch-panel-context-name"]')?.textContent).toContain(
      'AGT-W34',
    );
    expect(host.querySelector('[data-testid="orch-open-chats-live"]')?.textContent).toContain('2');
  });

  it('opens the sidebar Chats panel instead of a sheet-local session list', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorPanelHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorPanelHeaderComponent);
    let chatsRequested = 0;
    let contextToggled = 0;
    fixture.componentInstance.chatsRequested.subscribe(() => chatsRequested++);
    fixture.componentInstance.contextToggle.subscribe(() => contextToggled++);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    (host.querySelector('[data-testid="orch-open-chats-panel"]') as HTMLButtonElement).click();
    (host.querySelector('[data-testid="orch-context-badge"]') as HTMLButtonElement).click();

    expect(chatsRequested).toBe(1);
    expect(contextToggled).toBe(1);
    expect(host.querySelector('[data-testid="orch-context-count"]')).toBeNull();
    expect(host.querySelector('[data-testid="orch-open-chats-live"]')).toBeNull();
  });
});
