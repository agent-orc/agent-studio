import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi } from 'vitest';
import { OffStableGradesComponent, type OffStableGradedCard } from './off-stable-grades';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';

const URL = '/api/v1/management/host-releases/off-stable-grades';
const CARD: OffStableGradedCard = {
  taskKey: 'BH3-1', id: 'BH3-1', projectName: 'Batch', title: 'Header calm-down',
  lane: '5-human-review', attemptId: 'review_bh3', gradedAt: '2026-11-05T22:15:17Z',
  workerReleaseId: '20261105T1059Z-review-b9a3a90d6895',
  notice: 'graded by release 20261105T1059Z-review-b9a3a90d6895 (commit b9a3a90), which differs from Stable 0.9.5 (38b28d1)',
};

async function mount(reply: OffStableGradedCard[] | 'error') {
  const open = vi.fn();
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [OffStableGradesComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: TaskReferenceNavigationService, useValue: { openReferenceOrNotify: open } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(OffStableGradesComponent);
  const http = TestBed.inject(HttpTestingController);
  const request = http.expectOne(URL);
  if (reply === 'error') request.flush(null, { status: 500, statusText: 'Failed' });
  else request.flush(reply);
  fixture.detectChanges();
  http.verify();
  return { host: fixture.nativeElement as HTMLElement, open };
}

describe('OffStableGradesComponent (AGT-3845)', () => {
  it('lists each card a non-Stable review role graded, with a link to the card', async () => {
    const { host, open } = await mount([CARD]);
    expect(host.querySelector('[data-testid="off-stable-grades-count"]')?.textContent?.trim()).toBe('1');
    const row = host.querySelector('[data-testid="off-stable-grade-row"]')!;
    expect(row.getAttribute('data-task-key')).toBe('BH3-1');
    expect(row.querySelector('[data-testid="off-stable-grade-notice-text"]')?.textContent).toContain('differs from Stable 0.9.5');
    const link = row.querySelector<HTMLAnchorElement>('[data-testid="off-stable-grade-card"]')!;
    expect(link.getAttribute('href')).toBe('#/tasks/BH3-1');
    link.click();
    expect(open).toHaveBeenCalledWith('BH3-1');
  });

  it('renders nothing when no card was graded off Stable or the read failed', async () => {
    for (const reply of [[], 'error'] as const) {
      const { host } = await mount(reply === 'error' ? 'error' : [...reply]);
      expect(host.querySelector('[data-testid="off-stable-grades"]')).toBeNull();
    }
  });
});
