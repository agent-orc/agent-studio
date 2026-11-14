import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WatchdogReceiptComponent } from './watchdog-receipt.component';
import type { TaskInfo } from '../../../../models/task.model';

describe('WatchdogReceiptComponent', () => {
  it('loads the linked watchdog receipt and updates it after Undo', () => {
    TestBed.configureTestingModule({
      imports: [WatchdogReceiptComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(WatchdogReceiptComponent);
    fixture.componentRef.setInput('info', {
      id: 'AGT-1', parkedBlocker: { parkedAt: '2026-11-09T01:27:00Z' },
    } as TaskInfo);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(request => request.url === '/api/watcher/interventions'
      && request.params.get('card') === 'AGT-1').flush({ interventions: [{
      kind: 'auto-review-stranded', receipt: {
        id: 'receipt-1', why: 'No review owner.', undoable: true,
      },
    }] });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="parked-watchdog-receipt-link"]')?.getAttribute('href'))
      .toBe('#watchdog-receipt-receipt-1');
    root.querySelector<HTMLButtonElement>('[data-testid="parked-watchdog-undo"]')!.click();
    http.expectOne('/api/watcher/interventions/receipt-1/undo').flush({
      receipt: { id: 'receipt-1', why: 'No review owner.', undoable: false },
    });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="parked-watchdog-undo"]')).toBeNull();
    http.verify();
  });
});
