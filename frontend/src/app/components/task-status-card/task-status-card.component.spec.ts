import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { TaskStatusCardComponent, runStatusTone } from './task-status-card.component';
import type { TaskInfo } from '../../models/task.model';

function fixtureJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  const base: Partial<TaskInfo> = {
    id: 'demo-task',
    taskKey: 'C:/x::demo-task',
    key: 'ATP-001',
    title: 'A meaningful task title',
    state: '3-progress',
    order: 1,
    agent: 'claude',
    createdAt: new Date(Date.now() - 60_000).toISOString(),
    watchPath: 'C:/x',
    projectName: 'agent-taskboard',
    folderPath: 'C:/x/3-progress/demo-task',
    lastActivity: new Date(Date.now() - 60_000).toISOString(),
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
  };
  return { ...(base as TaskInfo), ...overrides };
}

describe('TaskStatusCardComponent', () => {
  it('renders chips, facts, and the title for a job', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });

    const fixture = TestBed.createComponent(TaskStatusCardComponent);
    fixture.componentRef.setInput('job', fixtureJob());
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-status-card-title"]')?.textContent).toContain(
      'A meaningful task title',
    );
    expect(root.querySelector('[data-testid="task-status-card-lane"]')?.textContent).toContain(
      'In Progress',
    );
    expect(root.querySelector('[data-testid="task-status-card-project"]')?.textContent).toContain(
      'agent-taskboard',
    );
    expect(root.querySelector('[data-testid="task-status-card-model"]')?.textContent).toContain(
      'claude-opus-4-7',
    );
  });

  it('falls back to "CLI default" when no model is set', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });

    const fixture = TestBed.createComponent(TaskStatusCardComponent);
    fixture.componentRef.setInput('job', fixtureJob({ model: null }));
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-status-card-model"]')?.textContent).toContain(
      'CLI default',
    );
  });

  it('hides the run line when no execution is recorded', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });

    const fixture = TestBed.createComponent(TaskStatusCardComponent);
    fixture.componentRef.setInput('job', fixtureJob({ execution: null }));
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-status-card-run"]')).toBeNull();
  });
});

describe('runStatusTone', () => {
  it('maps execution statuses and falls back to neutral', () => {
    expect(runStatusTone('running')).toBe('running');
    expect(runStatusTone('completed')).toBe('success');
    expect(runStatusTone('failed')).toBe('danger');
    expect(runStatusTone('stopped')).toBe('neutral');
    expect(runStatusTone('cancelled')).toBe('neutral');
    expect(runStatusTone('paused-later')).toBe('neutral');
    expect(runStatusTone(null)).toBe('neutral');
  });
});
