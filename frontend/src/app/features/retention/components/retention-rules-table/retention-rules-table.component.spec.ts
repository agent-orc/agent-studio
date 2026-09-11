import { provideZonelessChangeDetection } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PLATFORM_RETENTION_RULES, type RetentionPolicy } from '../../models/retention.model';
import { RetentionService } from '../../services/retention.service';
import { RetentionRulesTableComponent } from './retention-rules-table.component';

const policy = (version = 0): RetentionPolicy => ({
  scope: 'workspace', version, updatedAt: '2026-09-06T12:00:00Z', updatedBy: 'operator',
  rules: PLATFORM_RETENTION_RULES.map(rule => ({ ...rule })),
});

describe('RetentionRulesTableComponent', () => {
  const retention = {
    getWorkspacePolicy: vi.fn(), getProjectPolicy: vi.fn(), updateWorkspacePolicy: vi.fn(),
    updateProjectPolicy: vi.fn(), resetProjectPolicy: vi.fn(),
  };

  beforeEach(async () => {
    vi.resetAllMocks();
    retention.getWorkspacePolicy.mockReturnValue(of(policy()));
    retention.getProjectPolicy.mockReturnValue(of({ ...policy(), scope: 'project', rules: [] }));
    await TestBed.configureTestingModule({
      imports: [RetentionRulesTableComponent],
      providers: [provideZonelessChangeDetection(), { provide: RetentionService, useValue: retention }],
    }).compileComponents();
  });

  it('labels untouched workspace rules as platform defaults', async () => {
    const fixture = TestBed.createComponent(RetentionRulesTableComponent);
    fixture.detectChanges(); await fixture.componentInstance.reload(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Platform default');
  });

  it('shows a project override and its one-click reset', async () => {
    const heavy = { ...PLATFORM_RETENTION_RULES[2], archiveAfterDaysTerminal: 45 };
    retention.getProjectPolicy.mockReturnValue(of({ ...policy(2), scope: 'project', rules: [heavy] }));
    const fixture = TestBed.createComponent(RetentionRulesTableComponent);
    fixture.componentRef.setInput('scope', 'project'); fixture.componentRef.setInput('projectId', 'project-1');
    fixture.detectChanges(); await fixture.componentInstance.reload(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Project override');
    expect(fixture.nativeElement.querySelector('[data-testid="retention-rule-reset-full"]')).toBeTruthy();
  });

  it('reloads and reports the newer version after a save conflict', async () => {
    retention.updateWorkspacePolicy.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    retention.getWorkspacePolicy.mockReturnValueOnce(of(policy(1))).mockReturnValueOnce(of(policy(2)));
    const fixture = TestBed.createComponent(RetentionRulesTableComponent);
    fixture.detectChanges(); await fixture.whenStable();
    const row = fixture.componentInstance.rows.find(item => item.id === 'runtime')!;
    fixture.componentInstance.beginEdit(row);
    await fixture.componentInstance.save(row);
    expect(fixture.componentInstance.workspacePolicy()?.version).toBe(2);
    expect(fixture.componentInstance.message()).toContain('newer version');
  });
});
