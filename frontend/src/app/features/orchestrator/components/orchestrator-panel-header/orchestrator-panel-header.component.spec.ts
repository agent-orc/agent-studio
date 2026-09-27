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
    fixture.componentRef.setInput('contextCount', 17);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="orch-panel-context-type"]')?.textContent).toContain(
      'Dossier',
    );
    expect(host.querySelector('[data-testid="orch-panel-context-name"]')?.textContent).toContain(
      'AGT-W34',
    );
    expect(host.querySelector('[data-testid="orch-context-count"]')?.textContent).toContain('17');
  });

  it('emits the user usage toggle', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorPanelHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorPanelHeaderComponent);
    let toggles = 0;
    fixture.componentInstance.metadataToggle.subscribe(() => toggles++);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    (host.querySelector('[data-testid="orch-chat-metadata-toggle"]') as HTMLButtonElement).click();
    expect(toggles).toBe(1);
    fixture.componentRef.setInput('metadataEnabled', false);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="orch-chat-metadata-toggle"]')?.textContent).toContain('Show usage');
  });
});
