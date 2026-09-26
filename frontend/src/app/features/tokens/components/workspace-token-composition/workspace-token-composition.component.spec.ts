import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { WorkspaceTokenCompositionComponent } from './workspace-token-composition.component';

describe('WorkspaceTokenCompositionComponent', () => {
  it('keeps chat usage separate and excludes disabled projects', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkspaceTokenCompositionComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkspaceTokenCompositionComponent);
    fixture.componentRef.setInput('projects', [
      { project: 'active', agentTokens: 60, supportingTokens: 10, orchestratorTokens: 20, chatTokens: 10 },
      { project: 'disabled', agentTokens: 600, supportingTokens: 100, orchestratorTokens: 200, chatTokens: 100 },
    ]);
    fixture.componentRef.setInput('disabled', new Set(['disabled']));
    fixture.componentRef.setInput('total', 100);
    await fixture.whenStable();
    expect(fixture.componentInstance.amounts()).toEqual({
      agent: 60, supporting: 10, orchestrator: 20, chat: 10,
    });
    expect(fixture.nativeElement.querySelector('[data-testid="wtt-cat-chat"]')?.textContent).toContain('10');
  });
});
