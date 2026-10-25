import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenAnalysisQuotaComponent } from './token-analysis-quota';
import { fixtureResponse } from '../../testing/token-analysis.fixtures';
import * as util from '../../token-analysis.util';

describe('TokenAnalysisQuotaComponent', () => {
  it('renders its data', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisQuotaComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisQuotaComponent);
    const data = fixtureResponse();
    const days = util.dayRange(data.from, data.to);
    fixture.componentRef.setInput('lines', util.quotaLines(data.quota, days, null));
    fixture.componentRef.setInput('days', days.length);
    fixture.componentRef.setInput('windows', data.quota);
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('path').length).toBe(1);
    expect(root.querySelector('[data-testid="token-analysis-quota-table"]')?.textContent).toContain('running');
  });
});
