import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { liveSourcesFixture, liveStructureFixture } from '../live-structure.fixtures';
import { buildLiveScene } from '../live-structure.model';
import { LiveStructureDetailsComponent } from './live-structure-details.component';

const LAYERS = ['perf', 'network', 'storage', 'backup', 'warnings'] as const;

function setup(nodeId: string | null, linkId: string | null = null) {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const scene = buildLiveScene(liveStructureFixture, liveSourcesFixture);
  const fixture = TestBed.createComponent(LiveStructureDetailsComponent);
  fixture.componentRef.setInput('scene', scene);
  fixture.componentRef.setInput('node', nodeId ? scene.byId.get(nodeId) : null);
  fixture.componentRef.setInput('link', linkId ? scene.links.find(link => link.id === linkId) : null);
  fixture.componentRef.setInput('layers', { perf: true, network: true, storage: false, backup: false, warnings: true });
  fixture.detectChanges();
  const root = fixture.nativeElement as HTMLElement;
  const section = (layer: string): HTMLElement => root.querySelector<HTMLElement>(`[data-testid="live-details-${layer}"]`)!;
  return { fixture, root, section, scene };
}

describe('LiveStructureDetailsComponent', () => {
  it('gives each of the five layers its own section for a container, on or off', () => {
    const { root, section } = setup('machine:review-3');
    expect(root.querySelector('[data-testid="live-details-name"]')!.textContent).toBe('Review host 3');
    for (const layer of LAYERS) expect(section(layer), layer).not.toBeNull();
    expect(section('perf').textContent).toContain('shown on the canvas');
    expect(section('storage').textContent).toContain('layer is off');
    expect(section('perf').querySelector('[data-fact="cpu"]')!.textContent).toContain('4% of 24 cores');
    expect(section('storage').querySelector('[data-fact="mount:/srv/work"]')!.textContent).toContain('runner work');
    expect(section('network').textContent).toContain('Task Server connection');
    expect(section('network').textContent).toContain('5.1 ms');
    expect(section('backup').querySelector('[data-fact="data"]')!.textContent).toContain('not observed');
    expect(section('warnings').textContent).toContain('Missing toolchain:playwright');
    expect(section('warnings').textContent).toContain('AGT-10 refused');
    // The reported host name is in the card and nowhere else.
    expect(root.querySelector('[data-fact="technical"]')!.textContent).toContain('review-3');
    expect(root.querySelector('[data-fact="ports"] dd')!.classList.contains('details__muted')).toBe(true);
  });

  it('shows the installation when nothing is selected, with totals that add up', () => {
    const { root, section, scene } = setup(null);
    expect(root.querySelector('[data-testid="live-details-name"]')!.textContent).toBe('Agent Studio installation');
    expect(root.querySelector('[data-testid="live-details-kpis"]')!.textContent).toContain(String(scene.warnings.length));
    expect(section('warnings').querySelectorAll('.details__warning').length).toBe(scene.warnings.length);
    expect(section('backup').textContent).toContain('0 of 4 data locations');
    expect(root.querySelector('[data-testid="live-details-legend"]')).not.toBeNull();
    expect(root.querySelectorAll('[data-testid="live-details-levels"] li').length).toBe(4);
  });

  it('describes a connection with what is measured and says what is not', () => {
    const { root } = setup(null, 'link:machine:review-1>component:studio-server:api:control');
    expect(root.querySelector('[data-testid="live-details-name"]')!.textContent).toBe('Task Server connection');
    const text = root.textContent!;
    expect(text).toContain('Review host 1 to API');
    expect(text).toContain('4.5 ms');
    expect(text).toContain('https');
    const facts = Object.fromEntries([...root.querySelectorAll('dl > div')].map(row => [row.querySelector('dt')!.textContent, row.querySelector('dd')!.textContent]));
    expect(facts['Traffic']).toBe('not observed');
    expect(facts['Round trip']).toBe('4.5 ms');
  });
});
