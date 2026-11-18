import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CardTokenDrilldownComponent } from './card-token-drilldown';
import { fixtureDetail } from '../../testing/token-analysis.fixtures';
import { browserTimeZone, formatZonedWithUtc } from '../../../../services/usage-time.util';

describe('CardTokenDrilldownComponent', () => {
  it('renders its data', async () => {
    await TestBed.configureTestingModule({
      imports: [CardTokenDrilldownComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CardTokenDrilldownComponent);
    const detail = fixtureDetail();
    fixture.componentRef.setInput('detail', detail);
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('[data-testid="card-token-drilldown-summary"]')?.textContent).toContain('3 rounds');
    const gate = root.querySelector('[data-kind="gate"]');
    expect(gate?.textContent).toContain('1m 0s');
    expect(gate?.textContent).toContain('fail');
    expect(root.querySelector('.dd__when span')?.textContent?.trim())
      .toBe(formatZonedWithUtc(detail.events[0].at, browserTimeZone()));
  });
});
