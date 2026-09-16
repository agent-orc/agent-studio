import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { BeautifulResultsComponent } from './beautiful-results.component';
import { DossierReferenceHydratorService } from '../../../../services/dossier-reference-hydrator.service';
import {
  DOSSIER_FIXTURE_PATH,
  dossierChipsIn,
  provideDossierCatalogueStub,
} from '../../../../../testing/dossier-references';

describe('BeautifulResultsComponent', () => {
  async function mount(markdown: string, projectName: string | null = null): Promise<HTMLElement> {
    await TestBed.configureTestingModule({
      imports: [BeautifulResultsComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideDossierCatalogueStub()
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(BeautifulResultsComponent);
    fixture.componentRef.setInput('markdown', markdown);
    fixture.componentRef.setInput('jobId', 'demo');
    fixture.componentRef.setInput('watchPath', 'C:/repo');
    fixture.componentRef.setInput('projectName', projectName);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders a sentinel banner when markdown ends with a [[TASK_DONE]] token', async () => {
    const host = await mount('All good.\n\n[[TASK_DONE]]');
    const banner = host.querySelector('[data-testid="results-sentinel-banner"]');
    expect(banner).not.toBeNull();
    expect(banner?.getAttribute('data-kind')).toBe('done');
    // Body should not contain the raw sentinel any more.
    const body = host.querySelector('[data-testid="results-rendered"]');
    expect(body?.textContent ?? '').not.toMatch(/TASK_DONE/);
  });

  it('does not render Rendered/Raw pill buttons (F63: moved to context menu)', async () => {
    const host = await mount('# Hi');
    expect(host.querySelector('[data-testid="results-view-mode-rendered"]')).toBeNull();
    expect(host.querySelector('[data-testid="results-view-mode-raw"]')).toBeNull();
  });

  it('renders an empty placeholder when no body content remains after sentinel extraction', async () => {
    const host = await mount('[[TASK_NOOP]]');
    expect(host.querySelector('[data-testid="results-empty"]')).not.toBeNull();
  });

  it('replaces a broken image with a compact "missing" placeholder instead of an empty row', async () => {
    const host = await mount('## Images\n\n- ![](results/does-not-exist.png)');
    // Let the highlight/broken-image microtask attach its error listener.
    await Promise.resolve();
    const img = host.querySelector<HTMLImageElement>('img.results-figure__img');
    expect(img).not.toBeNull();
    img!.dispatchEvent(new Event('error'));

    const missing = host.querySelector('[data-testid="results-image-missing"]');
    expect(missing).not.toBeNull();
    expect(missing?.textContent ?? '').toContain('results/does-not-exist.png');
    // The broken <img> is gone, so no silently empty figure remains.
    expect(host.querySelector('img.results-figure__img')).toBeNull();
  });

  // AGT-2812: the Result view renders its own HTML rather than the shared
  // markdown element, so it opts its body into Dossier reference hydration.
  it('renders a named Dossier as a chip that opens the Dossier view', async () => {
    const host = await mount(`Decided in AGT-W54, see ${DOSSIER_FIXTURE_PATH}.`, 'Demo');
    TestBed.inject(DossierReferenceHydratorService).refresh();

    const chips = dossierChipsIn(host);
    expect(chips).toHaveLength(2);
    expect(chips[0].textContent).toContain('AGT-W54');
    expect(chips[0].textContent).toContain('Decision cards');
    expect(chips[0].querySelector('a')?.getAttribute('href'))
      .toContain('/workbenches/decision-cards');
    expect(host.querySelector('[data-testid="results-rendered"]')
      ?.getAttribute('data-dossier-reference-scope')).toBe('Demo');
  });
});
