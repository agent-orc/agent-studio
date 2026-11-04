import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { RetentionService } from '../../services/retention.service';
import { backupVerificationStatus } from '../retention-backups/retention-backups.component';
import { RetentionRunHistoryComponent, retentionRunResult } from './retention-run-history.component';

describe('RetentionRunHistoryComponent', () => {
  it('opens one disclosure row with the full report', async () => {
    const run = { id: 'run-1', startedAt: '2026-09-06T12:00:00Z', finishedAt: '2026-09-06T12:00:01Z', trigger: 'scheduled', mode: 'apply', policyVersion: 2, actionCount: 0, appliedBytes: 0, actorId: 'scheduler' };
    const plan = { plannedAt: run.startedAt, policyVersion: 2, actionCount: 0, totalBytes: 0, affectedTasks: 0, actions: [] };
    const retention = { listRuns: () => of([run]), getSchedule: () => of({ enabled: true, serverLocalHour: 3, nextRunAt: '2026-09-07T03:00:00Z' }), getRun: () => of({ summary: run, plan, errors: [], warnings: [] }) };
    await TestBed.configureTestingModule({ imports: [RetentionRunHistoryComponent], providers: [provideZonelessChangeDetection(), { provide: RetentionService, useValue: retention }] }).compileComponents();
    const fixture = TestBed.createComponent(RetentionRunHistoryComponent); fixture.detectChanges(); await fixture.componentInstance.reload(); fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="retention-history-toggle-run-1"]').click();
    await fixture.whenStable(); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="retention-history-detail"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="retention-plan-empty"]')).toBeTruthy();
    const schedule = fixture.nativeElement.querySelector('[data-testid="retention-schedule"]') as HTMLElement;
    expect(schedule.dataset['tone']).toBe('success');
    expect(schedule.textContent).toContain('Daily at 03:00 server time');
    const result = fixture.nativeElement.querySelector('[data-testid="retention-history-result"]') as HTMLElement;
    expect(result.dataset['tone']).toBe('success');
    expect(result.textContent?.trim()).toBe('Completed');
  });
});

describe('retention status mapping', () => {
  it('maps run results, keeping finished runs with errors out of danger', () => {
    expect(retentionRunResult(false, 0, 0)).toEqual({ tone: 'running', label: 'Running' });
    expect(retentionRunResult(true, 2, 1)).toEqual({ tone: 'warning', label: 'Completed with errors' });
    expect(retentionRunResult(true, 0, 1)).toEqual({ tone: 'warning', label: 'Completed with warnings' });
    expect(retentionRunResult(true, 0, 0)).toEqual({ tone: 'success', label: 'Completed' });
  });

  it('maps backup verification', () => {
    expect(backupVerificationStatus(true, 3)).toEqual({ tone: 'success', label: 'Verified' });
    expect(backupVerificationStatus(false, 1)).toEqual({ tone: 'warning', label: 'Needs attention' });
    expect(backupVerificationStatus(false, 0)).toEqual({ tone: 'neutral', label: 'Not checked' });
  });
});
