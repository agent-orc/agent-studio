import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { RetentionService } from '../../services/retention.service';
import { RetentionRunHistoryComponent } from './retention-run-history.component';

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
  });
});
