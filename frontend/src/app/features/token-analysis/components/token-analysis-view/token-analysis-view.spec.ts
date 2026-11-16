import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenAnalysisViewComponent } from './token-analysis-view';
import { fixtureDetail, fixtureResponse } from '../../testing/token-analysis.fixtures';
import { EMPTY_FILTERS, encodeAnalysisFilters } from '../../token-analysis.util';

const ANALYSIS = '/api/workspace/tokens/analysis';

describe('TokenAnalysisViewComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    history.replaceState(null, '', window.location.pathname + '#/workspace/settings/tokens/analysis');
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisViewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    history.replaceState(null, '', window.location.pathname);
  });

  async function render(fixedProject: string | null = null) {
    const fixture = TestBed.createComponent(TokenAnalysisViewComponent);
    fixture.componentRef.setInput('fixedProject', fixedProject);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  function el(fixture: { nativeElement: HTMLElement }, id: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  }

  it('renders cards, charts and a table whose total equals the visible children', async () => {
    const fixture = await render();
    const req = http.expectOne(r => r.url === ANALYSIS);
    expect(req.request.params.get('days')).toBe('30');
    expect(req.request.params.has('project')).toBe(false);
    req.flush(fixtureResponse());
    fixture.detectChanges();
    await fixture.whenStable();

    expect(el(fixture, 'token-analysis-total')?.textContent).toContain('2.6K');
    expect(el(fixture, 'token-analysis-table-total')?.textContent).toContain('2.6K');
    expect(el(fixture, 'token-analysis-in-flight')?.textContent?.trim()).toBe('1');
    expect(el(fixture, 'token-analysis-kind-bars')).not.toBeNull();
    expect(el(fixture, 'token-analysis-time-chart')?.querySelector('[data-incomplete="true"]')).not.toBeNull();
    expect(el(fixture, 'token-analysis-quota-table')?.textContent).toContain('100K');
    expect(el(fixture, 'token-analysis-row-rest')).not.toBeNull();
  });

  it('writes filters into the ta= hash segment and keeps the route', async () => {
    const fixture = await render();
    http.expectOne(r => r.url === ANALYSIS).flush(fixtureResponse());
    fixture.detectChanges();
    await fixture.whenStable();

    const select = el(fixture, 'token-analysis-filter-cli') as HTMLSelectElement;
    select.value = 'claude';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(window.location.hash.startsWith('#/workspace/settings/tokens/analysis&ta=')).toBe(true);
    expect(decodeURIComponent(window.location.hash)).toContain('cli=claude');
    expect(el(fixture, 'token-analysis-total')?.textContent).toContain('400');
  });

  it('restores filters and the drill-down from the URL', async () => {
    history.replaceState(null, '', window.location.pathname
      + '#/workspace/settings/tokens/analysis&ta='
      + encodeAnalysisFilters({ ...EMPTY_FILTERS, days: 7, card: 'Studio/job-1' }));
    const fixture = await render();
    const main = http.expectOne(r => r.url === ANALYSIS);
    expect(main.request.params.get('days')).toBe('7');
    main.flush(fixtureResponse());
    const detail = http.expectOne('/api/projects/Studio/token-usage/analysis/cards/job-1');
    detail.flush(fixtureDetail());
    fixture.detectChanges();
    await fixture.whenStable();

    const events = el(fixture, 'card-token-drilldown-events');
    expect(events?.querySelectorAll('li').length).toBe(2);
    expect(events?.textContent).toContain('10m 0s');
    expect(events?.textContent).toContain('reasoning 40');
  });

  it('ranks cards by cost, shows cost per usage kind, and keeps a tokens ranking in the URL (AGT-3805)', async () => {
    const data = fixtureResponse();
    // A cache-heavy card: most tokens, least cost.
    data.cards.push({ ...data.cards[1], jobId: 'job-3', key: 'AGT-3', inFlight: false, totalTokens: 50_000, costUsd: 0.02,
      slices: [{ ...data.cards[1].slices[0], input: 0, output: 0, cacheRead: 50_000, costUsd: 0.02 }] });
    const fixture = await render();
    http.expectOne(r => r.url === ANALYSIS).flush(data);
    fixture.detectChanges();
    await fixture.whenStable();

    const order = () => [...fixture.nativeElement.querySelectorAll('[data-testid^="token-analysis-kind-row-"]')]
      .map((b: Element) => b.textContent?.trim());
    expect(order()).toEqual(['AGT-1', 'AGT-2', 'AGT-3']);
    expect(el(fixture, 'token-analysis-kind-bars-measure')?.textContent).toContain('by theoretical cost');
    expect(el(fixture, 'token-analysis-kind-cost-coding')?.textContent).toContain('$0.620');
    expect(el(fixture, 'token-analysis-kind-cost-total')?.textContent).toContain('$0.880');

    el(fixture, 'token-analysis-measure-tokens')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(order()).toEqual(['AGT-3', 'AGT-1', 'AGT-2']);
    expect(el(fixture, 'token-analysis-kind-bars-measure')?.textContent).toContain('by tokens');
    expect(decodeURIComponent(window.location.hash)).toContain('sort=tokens');

    el(fixture, 'token-analysis-sort-cost')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(order()).toEqual(['AGT-1', 'AGT-2', 'AGT-3']);
    expect(window.location.hash).not.toContain('ta=');
  });

  it('shows the empty state for a range without usage', async () => {
    const fixture = await render();
    http.expectOne(r => r.url === ANALYSIS).flush({ ...fixtureResponse(), cards: [], remainders: [] });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(el(fixture, 'token-analysis-empty')?.textContent).toContain('No token usage in this range');
  });

  it('shows an error with a working retry', async () => {
    const fixture = await render();
    http.expectOne(r => r.url === ANALYSIS).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(el(fixture, 'token-analysis-error')).not.toBeNull();

    el(fixture, 'token-analysis-retry')!.click();
    http.expectOne(r => r.url === ANALYSIS).flush(fixtureResponse());
    fixture.detectChanges();
    await fixture.whenStable();
    expect(el(fixture, 'token-analysis-error')).toBeNull();
  });

  it('asks only for the fixed project in project scope and hides the project filter', async () => {
    const fixture = await render('Studio');
    expect(http.expectOne(r => r.url === ANALYSIS).request.params.get('project')).toBe('Studio');
    expect(el(fixture, 'token-analysis-filter-project')).toBeNull();
    expect(el(fixture, 'token-analysis-view')?.getAttribute('data-scope')).toBe('project');
  });
});
