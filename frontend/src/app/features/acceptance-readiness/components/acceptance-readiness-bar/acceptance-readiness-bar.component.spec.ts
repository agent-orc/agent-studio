import { beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AcceptanceReadinessBarComponent } from './acceptance-readiness-bar.component';
import { AcceptanceReadinessStore } from '../../state/acceptance-readiness.store';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { readinessItem, reviewCard } from '../../testing/acceptance-readiness.fixtures';

describe('AcceptanceReadinessBarComponent', () => {
  const confirm = { confirm: vi.fn() };
  const cards = [reviewCard('a'), reviewCard('b'), reviewCard('c')];

  beforeEach(() => {
    confirm.confirm.mockReset();
    TestBed.configureTestingModule({
      imports: [AcceptanceReadinessBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfirmDialogService, useValue: confirm },
      ],
    });
  });

  async function render() {
    const fixture = TestBed.createComponent(AcceptanceReadinessBarComponent);
    fixture.componentRef.setInput('jobs', cards);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/tasks/acceptance-readiness').flush({
      checkedAtUtc: '2026-11-09T10:00:00Z', counts: {},
      items: [readinessItem('a', 'ready'), readinessItem('b', 'review-concerns'), readinessItem('c', 'ready')],
    });
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, store: TestBed.inject(AcceptanceReadinessStore) };
  }

  it('checks the lane on render and names how many cards are ready', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="acceptance-readiness-summary"]')!.textContent!.replace(/\s+/g, ' ').trim())
      .toBe('2 of 3 ready to accept');
    expect(host.querySelector('[data-testid="acceptance-filter-ready"]')!.textContent).toContain('Ready 2');
    expect(host.querySelector('[data-testid="acceptance-filter-not-ready"]')!.textContent).toContain('Not ready 1');
    expect(host.querySelector('[data-testid="acceptance-accept-ready"]')!.textContent).toContain('Accept 2 ready');
  });

  it('accepts only the ready cards the filter shows, after the owner confirms', async () => {
    const { fixture, host, store } = await render();
    const accept = vi.spyOn(store, 'acceptReady').mockResolvedValue();

    host.querySelector<HTMLButtonElement>('[data-testid="acceptance-filter-not-ready"]')!.click();
    fixture.detectChanges();
    expect(host.querySelector<HTMLButtonElement>('[data-testid="acceptance-accept-ready"]')!.disabled).toBe(true);

    host.querySelector<HTMLButtonElement>('[data-testid="acceptance-filter-all"]')!.click();
    fixture.detectChanges();
    confirm.confirm.mockResolvedValue(false);
    await fixture.componentInstance.acceptReady();
    expect(accept).not.toHaveBeenCalled();

    confirm.confirm.mockResolvedValue(true);
    await fixture.componentInstance.acceptReady();
    expect(accept).toHaveBeenCalledWith([cards[0], cards[2]], cards);
  });

  it('lists each card\'s accept result', async () => {
    const { fixture, host, store } = await render();
    store.results.set([
      { jobId: 'a', title: 'Card a', accepted: true, message: 'Moved to Accepted' },
      { jobId: 'c', title: 'Card c', accepted: false, message: 'Acceptance refused: integrated-unverified.' },
    ]);
    fixture.detectChanges();

    const rows = host.querySelectorAll('[data-testid="acceptance-result"]');
    expect(rows.length).toBe(2);
    expect(rows[1].getAttribute('data-accepted')).toBe('false');
    expect(rows[1].textContent).toContain('Acceptance refused: integrated-unverified.');
    expect(host.querySelector('[data-testid="acceptance-results"]')!.textContent).toContain('1 of 2 accepted');
  });
});
