import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PLATFORM_RETENTION_RULES, type RetentionPlan } from '../../models/retention.model';
import { RetentionService } from '../../services/retention.service';
import { RetentionAdminComponent } from './retention-admin.component';

const emptyPlan: RetentionPlan = { plannedAt: '2026-09-06T12:00:00Z', policyVersion: 1, actionCount: 0, totalBytes: 0, affectedTasks: 0, actions: [] };

describe('RetentionAdminComponent preview', () => {
  const retention = {
    plan: vi.fn(), apply: vi.fn(), listRuns: vi.fn(() => of([])), getSchedule: vi.fn(() => of({ enabled: true, serverLocalHour: 3, nextRunAt: '' })), listFullBackups: vi.fn(() => of({ backups: [] })),
    getWorkspacePolicy: vi.fn(() => of({ scope: 'workspace', version: 1, updatedAt: '', updatedBy: '', rules: PLATFORM_RETENTION_RULES, fullBackups: null })),
  };
  beforeEach(async () => {
    vi.resetAllMocks(); retention.listRuns.mockReturnValue(of([])); retention.getSchedule.mockReturnValue(of({ enabled: true, serverLocalHour: 3, nextRunAt: '' })); retention.listFullBackups.mockReturnValue(of({ backups: [] }));
    retention.getWorkspacePolicy.mockReturnValue(of({ scope: 'workspace', version: 1, updatedAt: '', updatedBy: '', rules: PLATFORM_RETENTION_RULES, fullBackups: null }));
    await TestBed.configureTestingModule({ imports: [RetentionAdminComponent], providers: [provideZonelessChangeDetection(), { provide: RetentionService, useValue: retention }] }).compileComponents();
  });

  it('renders an explicit empty plan', async () => {
    retention.plan.mockReturnValue(of(emptyPlan));
    const fixture = TestBed.createComponent(RetentionAdminComponent); fixture.detectChanges();
    await fixture.componentInstance.preview(); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="retention-plan-empty"]')).toBeTruthy();
  });

  it('renders a populated sortable plan and enables the separate run action', async () => {
    retention.plan.mockReturnValue(of({ ...emptyPlan, actionCount: 1, affectedTasks: 1, totalBytes: 2048, actions: [{ kind: 'ArchiveHeavy', ruleId: 'heavy-stage-1', project: 'Demo', taskKey: 'DEM-1', taskId: '1', stage: 1, bytes: 2048, fileCount: 1, reason: 'age', lane: '6-done', terminalAt: '2026-08-01T00:00:00Z' }] }));
    const fixture = TestBed.createComponent(RetentionAdminComponent); fixture.detectChanges();
    await fixture.componentInstance.preview(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('DEM-1');
    expect(fixture.nativeElement.querySelector('[data-testid="retention-apply"]')).toBeTruthy();
  });
});
