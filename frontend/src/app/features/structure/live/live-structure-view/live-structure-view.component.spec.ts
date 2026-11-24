import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { liveSourcesFixture, liveStructureFixture } from '../live-structure.fixtures';
import { LiveStructureViewComponent } from './live-structure-view.component';

function setup(initialLayers: string | null = null) {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
  const fixture = TestBed.createComponent(LiveStructureViewComponent);
  fixture.componentRef.setInput('data', liveStructureFixture);
  fixture.componentRef.setInput('initialLayers', initialLayers);
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  const root = fixture.nativeElement as HTMLElement;
  return { fixture, http, root, view: root.querySelector<HTMLElement>('[data-testid="live-structure"]')! };
}

describe('LiveStructureViewComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('opens with performance, network and warnings on and reads the optional sources once', () => {
    const { fixture, http, view, root } = setup();
    expect(view.dataset['layers']).toBe('perf,network,warnings');
    http.expectOne('/api/operator/backups').flush(liveSourcesFixture.backup);
    http.expectOne('/api/v1/management/remote-hosts').flush(liveSourcesFixture.runners);
    http.expectOne('/api/v1/management/environment').flush(liveSourcesFixture.environment);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="live-layer-fact-backup"]')!.textContent).toBe('0 of 4 covered');
    expect(root.querySelector('[data-testid="live-layer-fact-warnings"]')!.textContent).toBe('8 open');
    http.verify();
  });

  it('says not observed when the optional sources cannot be read', () => {
    const { fixture, http, root } = setup();
    http.expectOne('/api/operator/backups').flush('', { status: 403, statusText: 'Forbidden' });
    http.expectOne('/api/v1/management/remote-hosts').flush('', { status: 403, statusText: 'Forbidden' });
    http.expectOne('/api/v1/management/environment').flush('', { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="live-layer-fact-backup"]')!.textContent).toBe('not observed');
    expect(root.querySelector('[data-testid="live-layer-fact-perf"]')!.textContent).toBe('5 of 12 slots busy');
  });

  it('switches layers by the visible switch and by the keys 1 to 5, and reports the state to remember', () => {
    const { fixture, view, root } = setup('backup');
    expect(view.dataset['layers']).toBe('backup');
    const remembered: { layers: string; focus: string | null }[] = [];
    fixture.componentRef.setInput('persist', (state: { layers: string; focus: string | null }) => remembered.push(state));
    root.querySelector<HTMLButtonElement>('[data-testid="live-layer-storage"]')!.click();
    fixture.detectChanges();
    expect(view.dataset['layers']).toBe('storage,backup');
    // The layer switched on last leads the order the zoom levels add facts in.
    expect(fixture.componentInstance.order()).toEqual(['storage', 'backup']);
    view.dispatchEvent(new KeyboardEvent('keydown', { key: '5', bubbles: true }));
    fixture.detectChanges();
    expect(view.dataset['layers']).toBe('storage,backup,warnings');
    expect(fixture.componentInstance.order()[0]).toBe('warnings');
    expect(remembered.at(-1)).toEqual({ layers: 'storage,backup,warnings', focus: null });
  });

  it('keeps outline, card details and selection in step', () => {
    const { fixture, view, root } = setup();
    root.querySelector<HTMLButtonElement>('[data-outline-id="machine:gate-1"]')!.click();
    fixture.detectChanges();
    expect(view.dataset['selected']).toBe('machine:gate-1');
    expect(root.querySelector('[data-testid="live-details-name"]')!.textContent).toBe('Gate host');
    expect(root.querySelector('[data-outline-id="machine:gate-1"]')!.getAttribute('aria-current')).toBe('true');
    root.querySelector<HTMLButtonElement>('[data-testid="live-details-close"]')!.click();
    fixture.detectChanges();
    expect(view.dataset['selected']).toBeUndefined();
    expect(root.querySelector('[data-testid="live-details-name"]')!.textContent).toBe('Agent Studio installation');
  });
});
