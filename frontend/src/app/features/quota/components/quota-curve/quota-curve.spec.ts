import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { QuotaCurveComponent } from './quota-curve';
import type { QuotaHistoryWindow } from '../../models/quota-history.model';

const window: QuotaHistoryWindow = {
  label: 'Current week (all models)',
  kind: 'weekly',
  points: [
    { at: '2026-09-29T01:27:00Z', usedPct: 77, resetAt: null },
    { at: '2026-09-29T04:27:00Z', usedPct: 83, resetAt: null },
  ],
  forecast: {
    status: 'full-before-reset', currentPct: 83, currentAt: '2026-09-29T04:27:00Z', ratePctPerHour: 2,
    rateFrom: '2026-09-29T01:27:00Z', forecastFullAt: '2026-09-29T12:57:00Z',
    resetAt: '2026-09-29T14:00:00Z', reachesFullBeforeReset: true,
  },
};

async function render() {
  await TestBed.configureTestingModule({
    imports: [QuotaCurveComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(QuotaCurveComponent);
  fixture.componentRef.setInput('window', window);
  fixture.componentRef.setInput('from', '2026-09-27T04:27:00Z');
  fixture.componentRef.setInput('to', '2026-09-29T04:27:00Z');
  fixture.detectChanges();
  return fixture;
}

describe('QuotaCurveComponent', () => {
  it('renders the line, the forecast, the now and reset markers, and a table view', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="quota-curve-line"]')?.getAttribute('d')).toMatch(/^M.+L/);
    expect(el.querySelector('[data-testid="quota-curve-forecast"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="quota-curve-now"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="quota-curve-reset"]')).not.toBeNull();
    expect(el.querySelectorAll('table tbody tr')).toHaveLength(2);
    expect(el.querySelector('svg')?.getAttribute('aria-label')).toContain('83% used');
  });

  it('shows a tooltip for the nearest reading while hovering', async () => {
    const fixture = await render();
    fixture.componentInstance.hover.set(1);
    fixture.detectChanges();
    const tooltip = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="quota-curve-tooltip"]');
    expect(tooltip?.textContent).toContain('83%');
    fixture.componentInstance.clearHover();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="quota-curve-tooltip"]')).toBeNull();
  });
});
