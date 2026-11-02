import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { GateSlotBoardComponent, type GateSlotBoard } from './gate-slot-board';

const board: GateSlotBoard = {
  total: 3,
  shortSlot: 2,
  rule: 'Slot 2 serves short gates (median of recent gates at most 10 min).',
  slots: [
    { slot: 0, shortSlot: false, project: 'agent-studio', gateId: 'build-test', durationClass: 'long', since: '2026-11-02T08:00:00Z' },
    { slot: 1, shortSlot: false, project: null, gateId: null, durationClass: null, since: null },
    { slot: 2, shortSlot: true, project: 'ai-patterns', gateId: 'build-test', durationClass: 'short', since: '2026-11-02T08:10:00Z' },
  ],
  waiting: [{
    project: 'agent-studio', gateId: 'merge-into-main', durationClass: 'long', since: '2026-11-02T08:20:00Z',
    reason: 'All 2 shared slots are busy; slot 2 is kept for short gates and opens to this long gate in 40 min.',
  }],
};

describe('GateSlotBoardComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [GateSlotBoardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows which slot runs what and why a gate waits', async () => {
    const fixture = TestBed.createComponent(GateSlotBoardComponent);
    fixture.componentRef.setInput('projectName', 'ai-patterns');
    fixture.detectChanges();

    http.expectOne('/api/system/gate-slots').flush(board);
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('[data-testid="gate-slot-board-busy"]')?.textContent).toContain('2/3');
    expect(element.querySelector('[data-testid="gate-slot-2"]')?.textContent).toContain('Short gates');
    expect(element.querySelector('[data-testid="gate-slot-2"]')?.textContent).toContain('ai-patterns');
    expect(element.querySelector('[data-testid="gate-slot-1"]')?.textContent).toContain('Free');
    expect(element.querySelector('[data-testid="gate-slot-waiter"]')?.textContent)
      .toContain('slot 2 is kept for short gates');
    fixture.destroy();
  });
});
