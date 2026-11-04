import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi } from 'vitest';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import { ProjectUrlContextHeaderComponent, integrationStatus } from './project-url-context-header';

describe('ProjectUrlContextHeaderComponent', () => {
  it('shows working-directory Git context and expandable linked open tasks', () => {
    const openTaskKey = vi.fn(() => true);
    TestBed.configureTestingModule({
      imports: [ProjectUrlContextHeaderComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskReferenceNavigationService, useValue: { openTaskKey } },
      ],
    });
    TestBed.inject(TaskService).jobs.set([
      task('PROJ-001::QST-42', 'QST-42', TaskState.Ready, 'Demo'),
      task('PROJ-001::QST-51', 'QST-51', TaskState.HumanReview, 'Demo'),
      task('PROJ-001::QST-12', 'QST-12', TaskState.Completed, 'Demo'),
      task('PROJ-002::ALT-1', 'ALT-1', TaskState.Ready, 'Other'),
    ]);

    const fixture = TestBed.createComponent(ProjectUrlContextHeaderComponent);
    fixture.componentRef.setInput('projectId', 'PROJ-001');
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('urlId', 'website');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/projects/PROJ-001/urls/website/context')
      .flush({
        projectName: 'Demo', repositoryName: 'quality-studio', workingDirectory: '/repo/web',
        repoRoot: '/repo', isRepo: true, branch: 'task/preview-context',
        headSha: 'abcdef0123456789abcdef0123456789abcdef01', headShortSha: 'abcdef01',
        comparisonRef: 'origin/develop', comparisonKind: 'integration', ahead: 2, behind: 1,
        isDirty: false, error: null,
      });
    fixture.detectChanges();

    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('[data-testid="url-preview-repository"]')?.textContent).toContain('quality-studio');
    expect(element.querySelector('[data-testid="url-preview-branch"]')?.textContent).toContain('task/preview-context');
    expect(element.querySelector('[data-testid="url-preview-head"]')?.textContent).toContain('abcdef01');
    expect(element.querySelector('[data-testid="url-preview-integration"]')?.textContent)
      .toContain('2 ahead, 1 behind origin/develop');
    expect(element.querySelector('[data-testid="url-preview-integration-state"]')?.getAttribute('data-tone')).toBe('warning');
    expect(element.querySelector('[data-testid="url-preview-tasks"]')?.textContent).toContain('2');

    (element.querySelector('[data-testid="url-preview-tasks"] summary') as HTMLElement).click();
    fixture.detectChanges();
    const links = [...element.querySelectorAll<HTMLButtonElement>('[data-testid="url-preview-task-link"]')];
    expect(links.map(link => link.textContent)).toEqual(expect.arrayContaining([
      expect.stringContaining('QST-42'), expect.stringContaining('QST-51'),
    ]));
    links[0].click();
    expect(openTaskKey).toHaveBeenCalledWith('PROJ-001::QST-42');
  });
});

function task(taskKey: string, key: string, state: string, projectName: string): TaskInfo {
  return {
    id: key.toLowerCase(), taskKey, key, title: `Task ${key}`, state, projectName,
    order: 0, agent: 'codex', createdAt: '2026-07-29T10:00:00Z', watchPath: '/tasks',
    folderPath: `/tasks/${key}`, lastActivity: '2026-07-29T10:00:00Z', sessionName: null,
    model: null, cliType: null, useOwnSession: null, lastUsage: null, execution: null, commit: null,
  };
}

describe('integrationStatus', () => {
  const base = { comparisonRef: 'origin/develop', ahead: 0, behind: 0, error: null };
  it('maps the integration line to tones', () => {
    expect(integrationStatus({ ...base })).toEqual({ tone: 'success', label: 'Aligned with origin/develop' });
    expect(integrationStatus({ ...base, ahead: 2, behind: 1 }).tone).toBe('warning');
    expect(integrationStatus({ ...base, comparisonRef: null })).toEqual({ tone: 'neutral', label: 'No comparison line' });
    expect(integrationStatus({ ...base, error: 'git failed' })).toEqual({ tone: 'neutral', label: 'Unavailable' });
    expect(integrationStatus(null).tone).toBe('neutral');
  });
});
