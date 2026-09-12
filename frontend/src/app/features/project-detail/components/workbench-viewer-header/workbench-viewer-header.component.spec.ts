import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { TaskReferenceStatus } from '../../../../components/task-reference-microcard/task-reference-microcard';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import type { WorkbenchDocument } from '../../../../models/project-docs.model';
import {
  implementationStatusFor,
  WorkbenchViewerHeaderComponent,
} from './workbench-viewer-header.component';

const DOCUMENT: WorkbenchDocument = {
  workbench: {
    id: 'viewer-header',
    key: 'AGT-W4',
    title: 'Compact viewer header with a deliberately long title',
    summary: 'Detailed context stays out of the normal header row.',
    status: 'decision-pending',
    phase: 'decision-ready',
    updatedAtUtc: '2026-08-09T10:00:00Z',
    entryPath: 'docs/operations/viewer-header/index.html',
    valid: true,
    error: null,
    sourceTaskKeys: ['AGT-10'],
    relatedTaskKeys: ['AGT-12'],
  },
  html: '<h1>Compact viewer header</h1>',
  branch: 'develop',
  revision: '1234567890abcdef',
  workingTreeModified: false,
  fingerprint: 'a'.repeat(64),
};

describe('WorkbenchViewerHeaderComponent', () => {
  it('derives implementation only between the first started card and all-terminal state', () => {
    const status = (key: string, lane: string | null, exists = true): TaskReferenceStatus => ({
      key,
      exists,
      taskKey: null,
      title: null,
      lane,
      projectId: 'PROJ-2',
      projectName: 'Agent Studio',
      projectColor: null,
      merge: null,
      reviewGrade: null,
    });

    expect(implementationStatusFor('decision-pending', [status('AGT-1', '3-progress')]))
      .toBe('In implementation');
    expect(implementationStatusFor('decided', [
      status('AGT-1', '6-completed'),
      status('AGT-2', '7-archive'),
    ])).toBeNull();
    expect(implementationStatusFor('decided', [
      status('AGT-1', '6-completed'),
      status('AGT-404', null, false),
    ])).toBe('In implementation');
    expect(implementationStatusFor('documented', [status('AGT-1', '3-progress')]))
      .toBeNull();
  });

  it('keeps the head compact and shows manual refresh only while live updates are disconnected', async () => {
    const statuses = [
      {
        key: 'AGT-10',
        exists: true,
        taskKey: 'Agent Studio::source-card',
        title: 'Source implementation card',
        lane: '5-human-review',
        projectId: 'PROJ-2',
        projectName: 'Agent Studio',
        projectColor: null,
        merge: null,
        reviewGrade: null,
      },
      {
        key: 'AGT-11',
        exists: true,
        taskKey: 'Agent Studio::linked-card',
        title: 'Linked implementation card',
        lane: '3-progress',
        projectId: 'PROJ-2',
        projectName: 'Agent Studio',
        projectColor: null,
        merge: null,
        reviewGrade: null,
      },
      {
        key: 'AGT-12',
        exists: true,
        taskKey: 'Agent Studio::legacy-card',
        title: 'Legacy descriptor card',
        lane: '2-ready',
        projectId: 'PROJ-2',
        projectName: 'Agent Studio',
        projectColor: null,
        merge: null,
        reviewGrade: null,
      },
    ];
    const getReferenceStatuses = vi.fn(() => of(statuses));
    const openTaskKey = vi.fn(() => true);

    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerHeaderComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: TaskService,
          useValue: {
            getReferenceStatuses,
          },
        },
        { provide: TaskReferenceNavigationService, useValue: { openTaskKey } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerHeaderComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.componentRef.setInput('document', DOCUMENT);
    fixture.componentRef.setInput('decisionPoints', [
      { id: 'route', kind: 'single', label: 'Route', options: [], commentLabel: null },
      { id: 'density', kind: 'single', label: 'Density', options: [], commentLabel: null },
      { id: 'proof', kind: 'confirm', label: 'Proof', options: [], commentLabel: null },
    ]);
    fixture.componentRef.setInput('liveConnected', true);
    fixture.componentRef.setInput('connectionChangedAtUtc', '2026-08-11T10:00:00Z');
    fixture.componentRef.setInput('lastUpdatedAtUtc', '2026-08-11T10:02:00Z');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Agent%20Studio/workbenches/AGT-W4/references').flush({
      projectName: 'Agent Studio',
      workbenchKey: 'AGT-W4',
      workbenchId: 'viewer-header',
      legacyTaskKeys: ['AGT-12'],
      items: [
        {
          sourceKey: 'AGT-11',
          sourceJobId: 'linked-card',
          sourceTitle: 'Linked implementation card',
          sourceState: '3-progress',
          sourceWatchPath: '/projects/agent-studio',
          kind: 'workbenches',
        },
      ],
    });
    fixture.detectChanges();

    expect(getReferenceStatuses).toHaveBeenCalledWith(['AGT-11', 'AGT-12', 'AGT-10']);
    const header = fixture.nativeElement.querySelector('[data-testid="workbench-viewer-header"]');
    expect(header.querySelector('[data-testid="workbench-viewer-title"]').textContent).toContain(
      'Compact viewer header',
    );
    expect(
      header.querySelector('[data-testid="workbench-viewer-open-decisions"]').textContent,
    ).toContain('3 open');
    expect(
      header
        .querySelector('[data-testid="workbench-viewer-tasks"]')
        .querySelectorAll('app-task-reference-microcard'),
    ).toHaveLength(3);
    expect(
      header.querySelector('[data-testid="workbench-viewer-implementation-status"]').textContent,
    ).toContain('In implementation');
    expect(header.querySelector('[data-testid="workbench-viewer-task-AGT-10"]')).toBeTruthy();

    const taskLink = header.querySelector(
      '[data-testid="workbench-viewer-task-AGT-11"] a',
    ) as HTMLAnchorElement;
    taskLink.click();
    expect(openTaskKey).toHaveBeenCalledWith('Agent Studio::linked-card');

    const popover = document.querySelector(
      '[data-testid="workbench-viewer-details-popover"]',
    ) as HTMLElement;
    const disclosure = header.querySelector('.viewer-head__details') as HTMLDetailsElement;
    expect(disclosure.open).toBe(false);
    (
      header.querySelector('[data-testid="workbench-viewer-details-trigger"]') as HTMLElement
    ).click();
    fixture.detectChanges();
    expect(disclosure.open).toBe(true);
    expect(popover.textContent).toContain(DOCUMENT.workbench.summary);
    expect(popover.textContent).toContain('docs/operations/viewer-header/index.html');
    expect(popover.querySelector('[data-testid="workbench-decision-panel"]')).toBeTruthy();
    expect(popover.querySelector('[data-testid="workbench-viewer-connection-state"]')?.textContent)
      .toContain('Connected since');
    expect(popover.querySelector('[data-testid="workbench-viewer-manual-refresh"]')).toBeNull();
    expect(header.querySelector('[data-testid="workbench-viewer-refresh"]')).toBeNull();
    expect(header.querySelector('[data-testid="workbench-viewer-stale-as-of"]')).toBeNull();

    const manualRefresh = vi.fn();
    fixture.componentInstance.manualRefresh.subscribe(manualRefresh);
    fixture.componentRef.setInput('liveConnected', false);
    fixture.detectChanges();

    expect(header.querySelector('[data-testid="workbench-viewer-stale-as-of"]')?.textContent)
      .toContain('Updates paused · as of');
    expect(popover.querySelector('[data-testid="workbench-viewer-connection-state"]')?.textContent)
      .toContain('Disconnected since');
    const fallback = popover.querySelector(
      '[data-testid="workbench-viewer-manual-refresh"]',
    ) as HTMLButtonElement;
    expect(fallback).toBeTruthy();
    fallback.click();
    expect(manualRefresh).toHaveBeenCalledOnce();

    fixture.destroy();
    http.verify();
  });

  it('records a relevance review through the Dossier endpoint', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerHeaderComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskService, useValue: { getReferenceStatuses: () => of([]) } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerHeaderComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.componentRef.setInput('document', DOCUMENT);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Agent%20Studio/workbenches/AGT-W4/references').flush({
      projectName: 'Agent Studio', workbenchKey: 'AGT-W4', workbenchId: 'viewer-header', legacyTaskKeys: [], items: [],
    });

    (fixture.nativeElement.querySelector('[data-testid="workbench-record-review"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const form = fixture.nativeElement.querySelector('[data-testid="workbench-review-form"]') as HTMLFormElement;
    const verdict = form.querySelector('[data-testid="workbench-review-verdict"]') as HTMLSelectElement;
    verdict.value = 'superseded';
    verdict.dispatchEvent(new Event('change'));
    const keys = form.querySelector('[data-testid="workbench-review-superseded-by"]') as HTMLInputElement;
    keys.value = 'AGT-W51';
    keys.dispatchEvent(new Event('input'));
    const note = form.querySelector('[data-testid="workbench-review-note"]') as HTMLTextAreaElement;
    note.value = 'A newer Dossier owns this decision.';
    note.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (form.querySelector('[data-testid="workbench-review-save"]') as HTMLButtonElement).click();

    const request = http.expectOne('/api/projects/Agent%20Studio/workbenches/viewer-header/review');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      verdict: 'superseded', supersededBy: ['AGT-W51'], reviewedBy: 'Operator', note: 'A newer Dossier owns this decision.',
    });
    request.flush({ success: true, workbenchId: 'viewer-header', review: request.request.body, revision: 'abc', error: null, errorCode: null });
    fixture.destroy();
    http.verify();
  });
});
