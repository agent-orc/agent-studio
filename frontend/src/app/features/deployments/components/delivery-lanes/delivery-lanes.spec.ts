import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it } from 'vitest';
import { DELIVERY_LANE_PREVIEW, DeliveryLanesComponent } from './delivery-lanes';

const card = (index: number) => ({
  key: `AGT-${String(index).padStart(4, '0')}`, title: `Card ${index}`, releaseVersion: null, deployedAtUtc: null,
});
const lanes = (waiting: number, production: number) => ({
  projectId: 'Agent Studio',
  lanes: [
    { environmentId: null, name: 'Waiting for release', order: -1, cards: Array.from({ length: waiting }, (_, index) => card(index + 1)) },
    { environmentId: 'studio-instance', name: 'Production', order: 0,
      cards: Array.from({ length: production }, (_, index) => ({ ...card(1000 + index), releaseVersion: '0.9.5', deployedAtUtc: '2026-11-16T03:35:00Z' })) },
  ],
});

async function render(response: ReturnType<typeof lanes> | 404, visibleKeys: ReadonlySet<string> | null = null) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(DeliveryLanesComponent);
  fixture.componentRef.setInput('project', 'Agent Studio');
  fixture.componentRef.setInput('heading', 'Agent Studio delivery');
  fixture.componentRef.setInput('visibleKeys', visibleKeys);
  fixture.detectChanges();
  await new Promise(resolve => setTimeout(resolve, 0));
  const request = TestBed.inject(HttpTestingController).expectOne('/api/projects/Agent%20Studio/deploy/delivery-lanes');
  if (response === 404) request.flush({ error: "Unknown project 'Agent Studio'" }, { status: 404, statusText: 'Not Found' });
  else request.flush(response);
  await fixture.whenStable();
  const root = fixture.nativeElement as HTMLElement;
  const lane = (id: string) => root.querySelector(`[data-testid="delivery-lane-Agent Studio-${id}"]`) as HTMLElement;
  const toggle = (id: string) => root.querySelector(`[data-testid="delivery-lane-toggle-Agent Studio-${id}"]`) as HTMLButtonElement | null;
  const cards = (id: string) => lane(id).querySelectorAll('[data-testid^="delivery-card-"]');
  return { fixture, root, lane, toggle, cards };
}

describe('DeliveryLanesComponent', () => {
  it('shows the count, the first cards and a Show all control for a long column', async () => {
    const { fixture, lane, toggle, cards } = await render(lanes(144, 3));

    // The header counts every card; only the preview is rendered.
    expect(lane('waiting').querySelector('h4')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('Waiting for release 144');
    expect(cards('waiting')).toHaveLength(DELIVERY_LANE_PREVIEW);
    expect(cards('waiting')[0].textContent).toContain('AGT-0001');
    expect(cards('waiting')[DELIVERY_LANE_PREVIEW - 1].textContent).toContain('AGT-0012');
    expect(lane('waiting').textContent).toContain('12 of 144 shown');
    const more = toggle('waiting')!;
    expect(more.textContent?.trim()).toBe('Show all 144');
    expect(more.getAttribute('aria-expanded')).toBe('false');
    expect(more.getAttribute('aria-controls')).toBe(lane('waiting').querySelector('.delivery-lanes__cards')?.id);

    more.click();
    await fixture.whenStable();
    expect(cards('waiting')).toHaveLength(144);
    expect(toggle('waiting')!.textContent?.trim()).toBe('Show first 12');
    expect(toggle('waiting')!.getAttribute('aria-expanded')).toBe('true');
    expect(lane('waiting').textContent).not.toContain('of 144 shown');

    toggle('waiting')!.click();
    await fixture.whenStable();
    expect(cards('waiting')).toHaveLength(DELIVERY_LANE_PREVIEW);
  });

  it('renders a short column in full without the control', async () => {
    const { lane, toggle, cards } = await render(lanes(DELIVERY_LANE_PREVIEW, 3));

    expect(cards('waiting')).toHaveLength(DELIVERY_LANE_PREVIEW);
    expect(toggle('waiting')).toBeNull();
    expect(cards('studio-instance')).toHaveLength(3);
    expect(toggle('studio-instance')).toBeNull();
    expect(lane('studio-instance').querySelector('button.delivery-lanes__header')?.textContent?.replace(/\s+/g, ' ').trim())
      .toBe('Delivery to Production 3');
  });

  it('expands one column without opening the others', async () => {
    const { fixture, toggle, cards } = await render(lanes(20, 30));

    toggle('studio-instance')!.click();
    await fixture.whenStable();
    expect(cards('studio-instance')).toHaveLength(30);
    expect(cards('waiting')).toHaveLength(DELIVERY_LANE_PREVIEW);
  });

  it('counts and previews only the cards the filter leaves', async () => {
    const visible = new Set(Array.from({ length: 5 }, (_, index) => card(index + 1).key.toLowerCase()));
    const { lane, toggle, cards } = await render(lanes(144, 0), visible);

    expect(lane('waiting').querySelector('h4')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('Waiting for release 5');
    expect(cards('waiting')).toHaveLength(5);
    expect(toggle('waiting')).toBeNull();
  });

  it('reports a lane read that failed', async () => {
    const { root } = await render(404);

    expect(root.textContent).toContain('Some delivery lanes could not be loaded.');
    expect(root.querySelector('[data-testid^="delivery-lane-"]')).toBeNull();
  });
});
