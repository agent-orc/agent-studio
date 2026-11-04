import { describe, expect, it } from 'vitest';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ProjectIntegrationView } from '../../../git';
import { ProjectIntegrationPanelComponent } from './project-integration-panel.component';

function response(): ProjectIntegrationView {
  return {
    project: 'Demo',
    isRepo: true,
    integrationRef: 'origin/develop',
    releaseRef: 'origin/main',
    integrationHeadSha: 'a'.repeat(40),
    releaseHeadSha: 'b'.repeat(40),
    capturedAt: '2026-07-22T10:00:00Z',
    queue: [
      { taskId: 'one', taskKey: 'AGT-1', title: 'Merged work', lane: '6-completed', stateSince: '2026-07-22T09:00:00Z', status: 'merged', mergeSha: 'a'.repeat(40), reason: null },
      { taskId: 'two', taskKey: 'AGT-2', title: 'Waiting work', lane: '6-completed', stateSince: '2026-07-22T09:01:00Z', status: 'waiting', mergeSha: null, reason: 'Not on origin/develop.' },
      { taskId: 'three', taskKey: 'AGT-3', title: 'Conflict work', lane: '6-completed', stateSince: '2026-07-22T09:02:00Z', status: 'conflict', mergeSha: null, reason: 'Conflict in app.ts.' },
      { taskId: 'four', taskKey: 'AGT-4', title: 'Docs only', lane: '6-completed', stateSince: '2026-07-22T09:03:00Z', status: 'skipped', mergeSha: null, reason: 'No integrable change set.' },
      { taskId: 'five', taskKey: 'AGT-5', title: 'Legacy delivery', lane: '7-archive', stateSince: '2026-07-22T09:04:00Z', status: 'legacy-unverifiable', mergeSha: null, reason: 'Archived review subject has no RunAttemptId.' },
      { taskId: 'six', taskKey: 'AGT-6', title: 'Retired delivery', lane: '7-archive', stateSince: '2026-07-22T09:05:00Z', status: 'superseded', mergeSha: null, reason: 'Archived delivery is no longer actionable.' },
    ],
    publisherMerges: [
      { taskKey: 'AGT-1', title: 'Merged work', sha: 'a'.repeat(40), shortSha: 'aaaaaaa', integratedAt: '2026-07-22T09:30:00Z', publisher: 'publisher', subject: 'merge(AGT-1): merged work' },
    ],
    promotion: {
      fromRef: 'origin/develop', toRef: 'origin/main', fromSha: 'a'.repeat(40), toSha: 'b'.repeat(40),
      tasks: [{ taskKey: 'AGT-1', title: 'Merged work', sha: 'a'.repeat(40), shortSha: 'aaaaaaa', subject: 'merge(AGT-1): merged work' }],
      files: [{ status: 'M', path: 'src/app.ts', added: 12, removed: 3 }],
      filesChanged: 1, added: 12, removed: 3,
    },
    error: null,
  };
}

describe('ProjectIntegrationPanelComponent', () => {
  it('renders and filters actionable and terminal queue states', () => {
    TestBed.configureTestingModule({
      imports: [ProjectIntegrationPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ProjectIntegrationPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url === '/api/git/integration' && request.params.get('project') === 'Demo').flush(response());
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('[data-testid="integration-queue-row"]')).toHaveLength(6);
    const statuses = Array.from(root.querySelectorAll<HTMLElement>('[data-testid="integration-queue-status"]'));
    expect(statuses.map(item => item.dataset['tone'])).toEqual(['success', 'info', 'danger', 'neutral', 'neutral', 'neutral']);
    expect(statuses.map(item => item.textContent?.trim())).toEqual(['Merged', 'Waiting', 'Conflict', 'Skipped', 'Legacy unverifiable', 'Superseded']);
    expect(root.textContent).toContain('1 merged');
    expect(root.textContent).toContain('1 legacy unverifiable');
    expect(root.textContent).toContain('1 superseded');
    expect(root.querySelector('[data-testid="integration-queue"]')?.textContent).toContain('Conflict in app.ts');
    expect(root.querySelector('[data-testid="promotion-diff"]')?.textContent).toContain('src/app.ts');
    expect(root.querySelector('[data-testid="promotion-diff"]')?.textContent).toContain('+12');
    expect(root.querySelector('[data-testid="publisher-merges"]')?.textContent).toContain('AGT-1');
    expect(root.querySelector('[data-testid="publisher-merges"]')?.textContent).toContain('publisher');
    expect(root.querySelector('[data-testid="integration-queue"] code[title]')?.getAttribute('title')).toBe('a'.repeat(40));
    expect(root.textContent).toContain('origin/develop');

    const conflictFilter = root.querySelector('[data-testid="integration-filter-conflict"]') as HTMLButtonElement;
    conflictFilter.click();
    fixture.detectChanges();
    expect(root.querySelectorAll('[data-testid="integration-queue-row"]')).toHaveLength(1);
    expect(root.querySelector('[data-testid="integration-queue"]')?.textContent).toContain('AGT-3');
    expect(root.querySelector('[data-testid="integration-queue"]')?.textContent).not.toContain('AGT-5');
    expect(conflictFilter.getAttribute('aria-pressed')).toBe('true');
    http.verify();
  });
});
