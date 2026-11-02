import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { BatchGateService } from '../../../services/batch-gate.service';
import { redHalvingState } from '../testing/batch-fixtures';
import { BatchMemberComponent } from './batch-member';

describe('BatchMemberComponent', () => {
  it('shows a fallback badge from state membership without reading the board card', async () => {
    const state = signal(redHalvingState);
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService,
      useValue: { state: () => state.asReadonly(), watch: () => vi.fn() },
    }] });
    const fixture = TestBed.createComponent(BatchMemberComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('taskKey', '/tmp/demo::AGT-4');
    fixture.componentRef.setInput('referenceKey', 'AGT-4');
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Per-task fallback');
    expect(fixture.nativeElement.querySelector('[data-status="fallback"]')).toBeTruthy();
  });

  it('names why an ejected member left its batch', async () => {
    const state = signal({
      ...redHalvingState,
      members: {
        ...redHalvingState.members,
        'AGT-9': {
          status: 'fallback' as const, batchId: 'batch-1', phase: 'published', position: 2, size: 3,
          reason: 'unattributable', reasonDetail: 'Immutable result ref does not resolve to the expected SHA.',
        },
      },
    });
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService,
      useValue: { state: () => state.asReadonly(), watch: () => vi.fn() },
    }] });
    const fixture = TestBed.createComponent(BatchMemberComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('taskKey', 'AGT-9');
    await fixture.whenStable();
    const badge = fixture.nativeElement.querySelector('[data-testid="batch-member-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('Per-task fallback · unattributable history');
    expect(badge.dataset['reason']).toBe('unattributable');
    expect(badge.title).toBe('Immutable result ref does not resolve to the expected SHA.');
  });

  it('shows a red pre-check and its reason on the card', async () => {
    const state = signal({
      ...redHalvingState,
      members: {
        ...redHalvingState.members,
        'AGT-10': {
          status: 'pre-check-red' as const,
          preCheck: { outcome: 'red' as const, resultSha: 'a'.repeat(40), baseSha: 'b'.repeat(40),
            gateProfileDigest: 'digest', reason: 'RoutePermissionInventoryTests: POST /api/v1/routes',
            failedTests: ['RoutePermissionInventoryTests'], completedAtUtc: '2026-11-02T18:00:00Z' },
        },
      },
    });
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService,
      useValue: { state: () => state.asReadonly(), watch: () => vi.fn() },
    }] });
    const fixture = TestBed.createComponent(BatchMemberComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('taskKey', 'AGT-10');
    await fixture.whenStable();
    const badge = fixture.nativeElement.querySelector('[data-testid="batch-member-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('Pre-check red');
    expect(badge.title).toContain('/api/v1/routes');
  });
});
