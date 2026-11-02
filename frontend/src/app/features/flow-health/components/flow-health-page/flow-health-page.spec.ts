import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, type TestRequest } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { FlowHealthPageComponent } from './flow-health-page';
import {
  batchSettingsPayload,
  executionInfrastructurePayload,
  flowHealthFixture,
  type FlowFixtureKind,
} from '../../testing/flow-health.fixtures';

const BASE = '/api/projects/Demo';

/** Every read the page makes, each of them free of Git process spawns. */
const ALLOWED = [
  '/api/gate-economics',
  `${BASE}/cycle-time`,
  `${BASE}/pipeline-health`,
  `${BASE}/batch-gate/report`,
  `${BASE}/batch-gate`,
  '/api/system/execution-infrastructure',
];

function mount(kind: FlowFixtureKind) {
  TestBed.configureTestingModule({
    imports: [FlowHealthPageComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(FlowHealthPageComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  // The page ages waits against the wall clock, so the fixture is stamped with it.
  const data = flowHealthFixture(kind, 'Demo', Date.now());
  const requests: TestRequest[] = http.match(() => true);
  for (const request of requests) {
    const url = path(request);
    const window = query(request, 'window') === '30d' ? '30d' : '7d';
    if (url === '/api/gate-economics') request.flush(data.economics[window]);
    else if (url === `${BASE}/cycle-time`) request.flush(data.cycleTime[window]);
    else if (url === `${BASE}/pipeline-health`) request.flush(data.pipelineHealth);
    else if (url === `${BASE}/batch-gate/report`) request.flush(data.batchReport);
    else if (url === `${BASE}/batch-gate`) request.flush(batchSettingsPayload(data.batchingEnabled ?? false));
    else if (url === '/api/system/execution-infrastructure') request.flush(executionInfrastructurePayload(data));
  }
  fixture.detectChanges();
  return { fixture, requests, host: fixture.nativeElement as HTMLElement };
}

function path(request: TestRequest): string { return request.request.url.split('?')[0]; }
function query(request: TestRequest, name: string): string | null {
  return new URL(request.request.urlWithParams, 'http://studio.test').searchParams.get(name);
}

afterEach(() => TestBed.inject(HttpTestingController).verify());

describe('FlowHealthPageComponent', () => {
  it('reads only the allow-listed projections, never the Git-backed batch state', () => {
    const { requests, fixture } = mount('healthyWeek');

    const urls = requests.map(path);
    expect(urls.every(url => ALLOWED.includes(url)), urls.join(', ')).toBe(true);
    expect(urls).not.toContain(`${BASE}/batch-gate/state`);
    // One gate-economics read per window: 7 days for the live queue and rollups, 30 days for the trend.
    expect(requests.filter(request => path(request) === '/api/gate-economics')
      .map(request => query(request, 'window')).sort()).toEqual(['30d', '7d']);
    fixture.destroy();
  });

  it('renders the brake, the four blocks and the stated targets for a review jam', () => {
    const { host, fixture } = mount('reviewJam');

    const brake = host.querySelector('[data-testid="flow-health-brake"]')!;
    expect(brake.getAttribute('data-tone')).toBe('brake');
    expect(brake.getAttribute('data-stage')).toBe('review');
    // Without host telemetry the review plane is unknown, so the slot reason falls back.
    expect(brake.textContent).toContain('Review waits: 14 cards, oldest 6 h');
    for (const block of ['now', 'grouping', 'gate', 'review']) {
      expect(host.querySelector(`[data-testid="flow-health-${block}"]`), block).not.toBeNull();
    }
    const review = host.querySelector('[data-testid="flow-health-stage"][data-stage="review"]')!;
    expect(review.getAttribute('data-status')).toBe('missed');
    const gateMinutes = host.querySelector('[data-figure="gateMinutesPerCard"]')!;
    expect(gateMinutes.querySelector('[data-testid="flow-health-target"]')!.textContent!.trim()).toBe('≤ 10 min');
    expect(gateMinutes.querySelector('[data-testid="flow-health-value-7d"]')!.textContent!.trim()).toBe('8 min');
    expect(host.querySelector('[data-testid="flow-health-batch-lifetime"]')!.textContent)
      .toContain('31 batches');
    fixture.destroy();
  });

  it('opens each block detail through one output', () => {
    const { host, fixture } = mount('gateJam');
    const opened: string[] = [];
    fixture.componentInstance.openDetail.subscribe(target => opened.push(target));

    for (const block of ['now', 'grouping', 'gate', 'review']) {
      (host.querySelector(`[data-testid="flow-health-detail-${block}"]`) as HTMLButtonElement).click();
    }
    expect(opened).toEqual(['cycle-time', 'steps', 'steps', 'overview']);
    fixture.destroy();
  });

  it('states that no data exists instead of showing zeros', () => {
    const { host, fixture } = mount('noData');

    expect(host.querySelector('[data-testid="flow-health-brake"]')!.getAttribute('data-tone')).toBe('empty');
    expect(host.querySelector('[data-figure="reviewRoundsPerCard"] [data-testid="flow-health-value-7d"]')!.textContent!.trim())
      .toBe('Unknown');
    fixture.destroy();
  });
});
