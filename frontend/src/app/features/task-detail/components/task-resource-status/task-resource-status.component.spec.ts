import { describe, expect, it, vi, beforeEach } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { TaskResourceStatusComponent } from './task-resource-status.component';
import { LayoutPanesService } from '../../services/layout-panes.service';
import { TaskSelectionService } from '../../state/task-selection.service';
import { idleResources } from '../../state/task-core.model';

describe('TaskResourceStatusComponent', () => {
  let selection: TaskSelectionService;
  let layout: LayoutPanesService;

  beforeEach(async () => {
    localStorage.removeItem('taskboard.panesVisible');
    await TestBed.configureTestingModule({
      imports: [TaskResourceStatusComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        LayoutPanesService,
      ],
    }).compileComponents();
    selection = TestBed.inject(TaskSelectionService);
    layout = TestBed.inject(LayoutPanesService);
    selection.resourceStates.set(idleResources());
  });

  it('requests Git only once its pane is visible, and again after a reset to idle', () => {
    const load = vi.spyOn(selection, 'loadResource').mockImplementation(name =>
      selection.resourceStates.update(states => ({ ...states, [name]: { phase: 'loading', reason: null } })));
    layout.panesVisible.set({ prompt: true, protocol: true, git: false });
    const fixture = TestBed.createComponent(TaskResourceStatusComponent);
    fixture.detectChanges();
    TestBed.tick();
    expect(load).not.toHaveBeenCalled();

    layout.panesVisible.set({ prompt: true, protocol: true, git: true });
    TestBed.tick();
    TestBed.tick();
    expect(load).toHaveBeenCalledTimes(1);
    expect(load).toHaveBeenCalledWith('git');

    // A new task or core generation resets Git to idle while the pane stays open.
    selection.resourceStates.set(idleResources());
    TestBed.tick();
    expect(load).toHaveBeenCalledTimes(2);
  });

  it('announces each unavailable resource with its reason and its own retry', () => {
    const load = vi.spyOn(selection, 'loadResource').mockImplementation(() => undefined);
    const retryDocuments = vi.spyOn(selection, 'retryDocuments').mockImplementation(() => undefined);
    layout.panesVisible.set({ prompt: true, protocol: true, git: false });
    selection.resourceStates.set({ ...idleResources(),
      git: { phase: 'unavailable', reason: 'git-snapshot-pending' },
      documents: { phase: 'error', reason: 'Document request failed' } });
    const fixture = TestBed.createComponent(TaskResourceStatusComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[aria-live="polite"]')).not.toBeNull();
    expect(root.textContent).toContain('Git information unavailable: The Git snapshot is still warming.');
    expect(root.querySelectorAll('[data-testid^="task-resource-retry-"]')).toHaveLength(2);
    root.querySelector<HTMLButtonElement>('[data-testid="task-resource-retry-git"]')!.click();
    root.querySelector<HTMLButtonElement>('[data-testid="task-resource-retry-documents"]')!.click();
    expect(load).toHaveBeenCalledWith('git');
    expect(retryDocuments).toHaveBeenCalledOnce();
  });
});
