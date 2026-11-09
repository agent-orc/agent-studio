import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ModelSelectionViewComponent, readSelectionFilters, selectionQuery } from './model-selection-view.component';

const active = [{ id: 'switch-1', at: '2026-11-05T10:00:00Z', kind: 'switched', consumer: 'coding',
  project: 'demo', taskKey: 'DEMO-1', from: { cliType: 'claude', model: 'opus-5', thinkingLevel: 'high' },
  to: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' }, rule: 'operator-switch',
  returnToPrimaryAt: '2026-11-06T10:00:00Z' }];

describe('ModelSelectionViewComponent', () => {
  let http: HttpTestingController;
  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ModelSelectionViewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    history.replaceState(null, '', `${location.pathname}#/workspace/settings/caps`);
  });

  it('loads the active strip and the empty state', () => {
    const fixture = TestBed.createComponent(ModelSelectionViewComponent);
    fixture.detectChanges();
    http.expectOne('/api/model-selection/active').flush(active);
    http.expectOne('/api/model-selection/effective').flush({ routes: [] });
    http.expectOne(req => req.url === '/api/model-selection/events').flush({ events: [], nextCursor: null });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Active fallbacks and health marks (1)');
    expect(text).toContain('1 affected card');
    expect(text).toContain('No selection events match these filters.');
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('.selection-view__actions button').length).toBe(2);
  });

  it('writes filter state to the URL and sends it to the API', () => {
    const fixture = TestBed.createComponent(ModelSelectionViewComponent);
    fixture.detectChanges();
    http.expectOne('/api/model-selection/active').flush([]);
    http.expectOne('/api/model-selection/effective').flush({ routes: [] });
    http.expectOne(req => req.url === '/api/model-selection/events').flush({ events: [], nextCursor: null });
    fixture.componentInstance.setFilter('provider', 'codex');
    const request = http.expectOne(req => req.url === '/api/model-selection/events');
    expect(request.request.params.get('provider')).toBe('codex');
    request.flush({ events: [], nextCursor: null });
    expect(new URLSearchParams(location.search).get('msProvider')).toBe('codex');
  });

  it('restores card and event links without a period bound', () => {
    const filters = readSelectionFilters('?msCard=DEMO-1&msEvent=event-1&msPeriod=24h');
    expect(filters.taskKey).toBe('DEMO-1');
    expect(filters.period).toBe('24h');
    expect(selectionQuery(filters)).toEqual({ taskKey: 'DEMO-1', eventId: 'event-1' });
  });

  it('keeps selected URL options visible when the event result is empty', () => {
    history.replaceState(null, '', `${location.pathname}?msProject=demo&msRule=quota-cap#/workspace/settings/caps`);
    const fixture = TestBed.createComponent(ModelSelectionViewComponent);
    fixture.detectChanges();
    http.expectOne('/api/model-selection/active').flush([]);
    http.expectOne('/api/model-selection/effective').flush({ routes: [] });
    http.expectOne(req => req.url === '/api/model-selection/events').flush({ events: [], nextCursor: null });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector<HTMLSelectElement>('[data-testid="selection-project"]')?.value).toBe('demo');
    expect(root.querySelector<HTMLSelectElement>('[data-testid="selection-rule"]')?.value).toBe('quota-cap');
  });
});
