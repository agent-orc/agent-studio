import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { BatchGateService } from '../../../services/batch-gate.service';
import { redHalvingState } from '../testing/batch-fixtures';
import { BatchMemberComponent } from './batch-member';

describe('BatchMemberComponent', () => {
  it('shows a fallback badge from state membership without reading the board card', async () => {
    const state = signal(redHalvingState);
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService,
      useValue: { state: () => state.asReadonly(), watch: () => () => {} },
    }] });
    const fixture = TestBed.createComponent(BatchMemberComponent);
    fixture.componentRef.setInput('project', 'Demo');
    fixture.componentRef.setInput('taskKey', 'AGT-4');
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Per-task fallback');
    expect(fixture.nativeElement.querySelector('[data-status="fallback"]')).toBeTruthy();
  });
});
