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
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="orch-panel-context-type"]')?.textContent).toContain(
      'Dossier',
    );
    expect(host.querySelector('[data-testid="orch-panel-context-name"]')?.textContent).toContain(
      'AGT-W34',
    );
    expect(host.querySelector('[data-testid="orch-context-badge"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="orch-open-chats-panel"]')).not.toBeNull();
  });

  it('separates the context actions toggle from the sidebar Chats affordance', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorPanelHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorPanelHeaderComponent);
    let actions = 0;
    let chats = 0;
    fixture.componentInstance.actionsToggle.subscribe(() => actions++);
    fixture.componentInstance.chatsPanelRequest.subscribe(() => chats++);
    fixture.componentRef.setInput('actionsOpen', true);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const toggle = host.querySelector<HTMLButtonElement>('[data-testid="orch-context-badge"]')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(host.querySelector('[data-testid="orch-context-count"]')).toBeNull();
    toggle.click();
    host.querySelector<HTMLButtonElement>('[data-testid="orch-open-chats-panel"]')!.click();
    expect(actions).toBe(1);
    expect(chats).toBe(1);
  });
});
