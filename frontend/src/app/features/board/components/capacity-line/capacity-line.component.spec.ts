import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CapacityLineComponent } from './capacity-line.component';
import { vi } from 'vitest';

describe('CapacityLineComponent', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ imports: [CapacityLineComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
  });
  afterEach(() => { TestBed.resetTestingModule(); vi.useRealTimers(); });

  it('switches from fleet to the selected project and clears a failed reading', async () => {
    const fixture = TestBed.createComponent(CapacityLineComponent);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    const requests = TestBed.inject(HttpTestingController);
    requests.expectOne('/api/operator/coding-capacity').flush({
      fleet: { totalSlots: 12, usedSlots: 5, ready: 19, claimableNow: 3, dependencyWidth: 3 },
      projects: [{ project: 'alpha', totalSlots: 4, usedSlots: 1, ready: 2, claimableNow: 2, dependencyWidth: 2 }],
    });
    expect(fixture.componentInstance.line()).toBe('5 of 12 slots, 3 claimable, limited by 3 chains');
    fixture.componentRef.setInput('project', 'alpha');
    expect(fixture.componentInstance.line()).toBe('1 of 4 slots, 2 claimable');
    await vi.advanceTimersByTimeAsync(30_000);
    requests.expectOne('/api/operator/coding-capacity').flush({}, { status: 503, statusText: 'Unavailable' });
    expect(fixture.componentInstance.line()).toBe('');
    fixture.destroy();
    await vi.advanceTimersByTimeAsync(30_000);
    requests.verify();
  });
});
