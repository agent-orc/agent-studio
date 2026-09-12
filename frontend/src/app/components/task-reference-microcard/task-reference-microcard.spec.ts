import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';
import { TaskReferenceMicrocardComponent, TaskReferenceStatus } from './task-reference-microcard';

const status: TaskReferenceStatus = {
  key: 'AGT-2050',
  exists: true,
  taskKey: 'PROJ-001::task',
  title: 'Living references',
  lane: '3-progress',
  projectId: 'PROJ-001',
  projectName: 'Agent Studio',
  projectColor: '#a78bfa',
  merge: {
    inIntegration: true,
    inRelease: false,
    integrationBranch: 'develop',
    releaseBranch: 'main',
  },
  reviewGrade: 'A',
};

describe('TaskReferenceMicrocardComponent', () => {
  it('renders an accessible task link, lane, merge state and popover detail', async () => {
    const openTaskKey = vi.fn(() => true);
    await TestBed.configureTestingModule({
      imports: [TaskReferenceMicrocardComponent],
      providers: [{ provide: TaskReferenceNavigationService, useValue: { openTaskKey } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskReferenceMicrocardComponent);
    fixture.componentRef.setInput('status', status);
    fixture.detectChanges();

    const link = fixture.nativeElement.querySelector('a') as HTMLAnchorElement;
    expect(link.getAttribute('aria-label')).toContain('Open task AGT-2050');
    expect(fixture.componentInstance.tooltipLabel()).toContain('Review grade A');
    expect(fixture.componentInstance.tooltipLabel()).toContain('develop: merged · main: open');
    expect(fixture.nativeElement.querySelector('.task-ref__popover')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.task-ref__merge-on')).toHaveLength(1);
    link.click();
    expect(openTaskKey).toHaveBeenCalledWith('PROJ-001::task');
  });

  it('shows key, title, and lane together for a primary reference receipt', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskReferenceMicrocardComponent],
      providers: [{ provide: TaskReferenceNavigationService, useValue: { openTaskKey: vi.fn() } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskReferenceMicrocardComponent);
    fixture.componentRef.setInput('status', { ...status, lane: '1-preparation' });
    fixture.componentRef.setInput('expanded', true);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('a').textContent).toContain('AGT-2050');
    expect(fixture.nativeElement.querySelector('a').textContent).toContain('Living references');
    expect(fixture.nativeElement.querySelector('a').textContent).toContain('Preparation');
  });

  it('renders an unknown registry key as a non-link ghost', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskReferenceMicrocardComponent],
      providers: [{ provide: TaskReferenceNavigationService, useValue: { openTaskKey: vi.fn() } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskReferenceMicrocardComponent);
    fixture.componentRef.setInput('status', {
      ...status,
      exists: false,
      taskKey: null,
      title: null,
      lane: null,
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('a')).toBeNull();
    expect(fixture.nativeElement.querySelector('.task-ref--ghost')).toBeTruthy();
    expect(fixture.componentInstance.tooltipLabel()).toContain('Key: AGT-2050');
    expect(fixture.componentInstance.tooltipLabel()).toContain('State: Unknown or deleted task');
  });

  it('keeps task navigation and tooltip detail in the compact lane-dot variant', async () => {
    const openTaskKey = vi.fn(() => true);
    await TestBed.configureTestingModule({
      imports: [TaskReferenceMicrocardComponent],
      providers: [{ provide: TaskReferenceNavigationService, useValue: { openTaskKey } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskReferenceMicrocardComponent);
    fixture.componentRef.setInput('status', status);
    fixture.componentRef.setInput('variant', 'lane-dot');
    fixture.componentRef.setInput('testId', 'linked-task-AGT-2050');
    fixture.detectChanges();

    const host = fixture.nativeElement.querySelector('[data-testid="linked-task-AGT-2050"]');
    const link = host.querySelector('a') as HTMLAnchorElement;
    expect(host.querySelector('.task-ref__lane-dot')).toBeTruthy();
    expect(link.getAttribute('data-lane-tone')).toBe('progress');
    expect(fixture.componentInstance.tooltipLabel()).toContain('Key: AGT-2050');
    expect(fixture.componentInstance.tooltipLabel()).toContain('Living references');
    expect(fixture.componentInstance.tooltipLabel()).toContain('Lane: In Progress');
    expect(fixture.componentInstance.tooltipLabel()).toContain('State: A run is executing the task');
    expect(host.textContent).not.toContain('AGT-2050●');
    link.click();
    expect(openTaskKey).toHaveBeenCalledWith('PROJ-001::task');
  });
});
