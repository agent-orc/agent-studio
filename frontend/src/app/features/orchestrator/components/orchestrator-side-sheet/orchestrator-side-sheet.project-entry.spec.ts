import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { UiPreferencesService } from '../../../shell/state/ui-preferences.service';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

describe('OrchestratorSideSheetComponent project entry', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    TestBed.configureTestingModule({
      imports: [OrchestratorSideSheetComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
  });

  it('applies a page chat project request when the deferred sheet mounts', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Alpha', 'Beta']);
    fixture.componentRef.setInput('preferredProject', 'Alpha');
    fixture.componentRef.setInput('requestedProject', { projectName: 'Beta' });
    fixture.componentRef.setInput('composerContext', { project: 'Alpha', surface: 'Board' });
    fixture.detectChanges();

    expect(fixture.componentInstance.activeProject()).toBe('Beta');
  });

  it('applies a repeated page chat request after the sheet changes project', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Alpha', 'Beta']);
    fixture.componentRef.setInput('preferredProject', 'Alpha');
    fixture.componentRef.setInput('requestedProject', { projectName: 'Alpha' });
    fixture.detectChanges();

    fixture.componentInstance.setActiveProject('Beta');
    expect(fixture.componentInstance.contextKey()).toBe('project:Beta');

    fixture.componentRef.setInput('requestedProject', { projectName: 'Alpha' });
    fixture.detectChanges();
    expect(fixture.componentInstance.contextKey()).toBe('project:Alpha');
  });

  it('opens only for the shell standard-entry event and uses its resolved project', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Persisted project', 'Resolved project']);
    fixture.componentRef.setInput('projectEntryRevision', 0);
    fixture.componentRef.setInput('preferredProject', 'Persisted project');
    fixture.componentRef.setInput('composerContext', { project: 'Persisted project', surface: 'Board' });
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);

    fixture.componentRef.setInput('preferredProject', 'Resolved project');
    fixture.componentRef.setInput('composerContext', { project: 'Resolved project', surface: 'Overview' });
    fixture.componentRef.setInput('projectEntryRevision', 1);
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(true);
    expect(fixture.componentInstance.activeProject()).toBe('Resolved project');
    expect(fixture.componentInstance.contextKey()).toBe('project:Resolved project');
    expect(document.activeElement).not.toBe(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="chat-input"]'),
    );
  });

  it('respects the saved opt-out and leaves the status-bar toggle path available', () => {
    TestBed.inject(UiPreferencesService).setOpenProjectChatOnEntry(false);
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Quiet project']);
    fixture.componentRef.setInput('projectEntryRevision', 0);
    fixture.componentRef.setInput('preferredProject', 'Quiet project');
    fixture.componentRef.setInput('composerContext', { project: 'Quiet project', surface: 'Board' });
    fixture.detectChanges();
    fixture.componentRef.setInput('projectEntryRevision', 1);
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);
    fixture.componentInstance.toggle();
    expect(fixture.componentInstance.open()).toBe(true);
  });

  it('does not turn task navigation into the project side-sheet entry', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Task project']);
    fixture.componentRef.setInput('preferredProject', 'Task project');
    fixture.componentRef.setInput('composerContext', {
      project: 'Task project',
      surface: 'Task',
      taskKey: 'AGT-2576',
      taskId: 'agt-2576',
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);
  });

  it('keeps a manually closed panel closed across page and project navigation', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Alpha', 'Beta']);
    fixture.componentRef.setInput('projectEntryRevision', 0);
    fixture.componentRef.setInput('preferredProject', 'Alpha');
    fixture.componentRef.setInput('composerContext', { project: 'Alpha', surface: 'Board' });
    fixture.detectChanges();
    fixture.componentRef.setInput('projectEntryRevision', 1);
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);

    fixture.componentInstance.hide();
    fixture.componentRef.setInput('composerContext', { project: 'Alpha', surface: 'Wiki' });
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);

    fixture.componentRef.setInput('preferredProject', 'Beta');
    fixture.componentRef.setInput('composerContext', { project: 'Beta', surface: 'Board' });
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);
    expect(fixture.componentInstance.contextKey()).toBe('project:Beta');
  });

  it('restores a visible panel without treating later navigation as an open command', () => {
    sessionStorage.setItem('atp.studio.orchestratorOpen.v1', '1');
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Alpha', 'Beta']);
    fixture.componentRef.setInput('projectEntryRevision', 0);
    fixture.componentRef.setInput('preferredProject', 'Alpha');
    fixture.componentRef.setInput('composerContext', { project: 'Alpha', surface: 'Board' });
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(true);
    fixture.componentRef.setInput('preferredProject', 'Beta');
    fixture.componentRef.setInput('composerContext', { project: 'Beta', surface: 'Dossier', detail: 'Runtime review' });
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(true);
    expect(fixture.componentInstance.contextKey()).toBe('project:Beta');
  });
});
