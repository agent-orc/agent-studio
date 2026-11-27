import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { AuthService } from '../../../../services/auth.service';
import { OrchestratorFeedEntryComponent } from './orchestrator-feed-entry';

describe('OrchestratorFeedEntryComponent', () => {
  it('restores an elevated strategy as the signed-in Owner from the immutable sequence', async () => {
    await TestBed.configureTestingModule({ imports: [OrchestratorFeedEntryComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
      { provide: AuthService, useValue: { status: signal({ user: { role: 'owner' } }) } },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorFeedEntryComponent);
    fixture.componentRef.setInput('entry', { ts: '2026-11-27T10:00:00Z', kind: 'action', topic: 'operator-elevated-action',
      summary: 'Testing strategy: fast to strict', project: 'Website', testingUndoSequence: 42 });
    fixture.detectChanges();
    const button = [...fixture.nativeElement.querySelectorAll('button')].find((item: unknown) => (item as HTMLButtonElement).textContent?.trim() === 'Undo') as HTMLButtonElement;
    expect(button).toBeTruthy();
    button.click();
    const http = TestBed.inject(HttpTestingController);
    const undo = http.expectOne('/api/projects/Website/testing/undo/42');
    expect(undo.request.body).toEqual({});
    undo.flush({ strategy: 'fast' });
    await fixture.whenStable();
    expect(button.textContent).toContain('Restored');
    expect(button.disabled).toBe(true);
    http.verify(); fixture.destroy();
  });
  it('renders project identity and emits project filtering without selecting the row', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorFeedEntryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorFeedEntryComponent);
    fixture.componentRef.setInput('entry', {
      ts: '2026-08-10T08:00:00Z', kind: 'decision', topic: 'watcher/decision',
      summary: 'Decision required', project: 'Agent Studio', jobId: null, tokenUsage: null,
    });
    const projects: string[] = [];
    const selections: unknown[] = [];
    fixture.componentInstance.projectFilterRequest.subscribe(project => projects.push(project));
    fixture.componentInstance.selectRequest.subscribe(entry => selections.push(entry));
    fixture.detectChanges();

    const chip = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="orchestrator-entry-project"]')!;
    expect(chip.textContent?.trim()).toBe('AS');
    chip.click();
    expect(projects).toEqual(['Agent Studio']);
    expect(selections).toEqual([]);
  });
});
