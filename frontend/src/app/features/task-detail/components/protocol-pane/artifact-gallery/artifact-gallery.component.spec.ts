import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { MediaLightboxService } from '../../../../../services/media-lightbox.service';
import { ArtifactGalleryComponent } from './artifact-gallery.component';
import type { ConversationArtifact } from './artifact-gallery.model';

const IMAGE = (id: string): ConversationArtifact => ({
  id,
  kind: 'image',
  path: `results/${id}.png`,
  fileName: `${id}.png`,
  label: `${id}.png`,
  url: `/api/results/${id}.png`,
  contentUrl: null,
});

function documentArtifact(kind: ConversationArtifact['kind'], extension: string): ConversationArtifact {
  return {
    id: kind,
    kind,
    path: `results/delivery.${extension}`,
    fileName: `delivery.${extension}`,
    label: `delivery.${extension}`,
    url: `/api/results/delivery.${extension}`,
    contentUrl: `/api/files/results/delivery.${extension}`,
  };
}

describe('ArtifactGalleryComponent', () => {
  it('uses listing dimensions and shows a large image placeholder without fetching its pixels', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ArtifactGalleryComponent);
    fixture.componentRef.setInput('artifacts', [
      { ...IMAGE('small'), url: '/api/tasks/job-1/screenshot?path=small.png' },
      { ...IMAGE('large'), url: '/api/tasks/job-1/screenshot?path=large.png' },
    ]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('img')).toHaveLength(0);
    TestBed.inject(HttpTestingController).expectOne('/api/tasks/job-1/screenshots').flush({ screenshots: [
      { relativePath: 'results/small.png', sizeBytes: 94_000, width: 1200, height: 800 },
      { relativePath: 'results/large.png', sizeBytes: 2_500_000, width: 3000, height: 2000 },
    ] });
    await fixture.whenStable();
    const images = fixture.nativeElement.querySelectorAll('img') as NodeListOf<HTMLImageElement>;
    expect(images).toHaveLength(1);
    expect(images[0].getAttribute('width')).toBe('1200');
    expect(images[0].getAttribute('height')).toBe('800');
    expect(fixture.nativeElement.querySelector('[data-testid="artifact-gallery-large-placeholder"]')?.textContent)
      .toContain('2.5 MB');
  });

  it('renders lazy original URLs and opens them in the lightbox', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ArtifactGalleryComponent);
    fixture.componentRef.setInput('artifacts', [IMAGE('light'), IMAGE('dark')]);
    await fixture.whenStable();

    const thumbnails = [...fixture.nativeElement.querySelectorAll('img')] as HTMLImageElement[];
    expect(thumbnails.map((image) => image.getAttribute('src'))).toEqual([
      '/api/results/light.png', '/api/results/dark.png',
    ]);
    expect(thumbnails.every((image) => image.getAttribute('loading') === 'lazy')).toBe(true);
    expect(thumbnails.every((image) => image.getAttribute('decoding') === 'async')).toBe(true);
    expect(thumbnails.every((image) => image.getAttribute('width') === '360')).toBe(true);

    (fixture.nativeElement.querySelector('[data-testid="artifact-gallery-thumbnail"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    const lightbox = TestBed.inject(MediaLightboxService);
    expect(lightbox.count()).toBe(2);
    expect(lightbox.active()?.src).toBe('/api/results/light.png');
    expect(lightbox.active()?.alt).toBe('light.png · results/light.png');
  });

  it('loads and renders a typed diff preview on demand', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ArtifactGalleryComponent);
    fixture.componentRef.setInput('artifacts', [documentArtifact('diff', 'diff')]);
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('[data-testid="artifact-document-toggle-diff"]') as HTMLButtonElement).click();
    const request = TestBed.inject(HttpTestingController).expectOne('/api/files/results/delivery.diff');
    request.flush('@@ -1 +1 @@\n-old\n+new');
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('@@ -1 +1 @@');
    expect(fixture.nativeElement.querySelector('[data-line-kind="delete"]')?.textContent).toContain('-old');
    expect(fixture.nativeElement.querySelector('[data-line-kind="add"]')?.textContent).toContain('+new');
  });

  it('preserves an expanded preview while the polled gallery view is remounted', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const artifact = documentArtifact('diff', 'diff');
    const first = TestBed.createComponent(ArtifactGalleryComponent);
    first.componentRef.setInput('artifacts', [artifact]);
    await first.whenStable();

    (first.nativeElement.querySelector('[data-testid="artifact-document-toggle-diff"]') as HTMLButtonElement).click();
    const request = TestBed.inject(HttpTestingController).expectOne('/api/files/results/delivery.diff');
    first.destroy();

    const replacement = TestBed.createComponent(ArtifactGalleryComponent);
    replacement.componentRef.setInput('artifacts', [artifact]);
    await replacement.whenStable();
    expect(replacement.nativeElement.textContent).toContain('Loading preview...');

    request.flush('@@ -1 +1 @@\n-old\n+survives remount');
    await replacement.whenStable();
    expect(replacement.nativeElement.textContent).toContain('+survives remount');
  });

  it('opens HTML through the artifact URL instead of embedding another viewer', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ArtifactGalleryComponent);
    fixture.componentRef.setInput('artifacts', [documentArtifact('html', 'html')]);
    await fixture.whenStable();
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);

    (fixture.nativeElement.querySelector('[data-testid="artifact-document-toggle-html"]') as HTMLButtonElement).click();

    expect(open).toHaveBeenCalledWith('/api/results/delivery.html', '_blank', 'noopener,noreferrer');
    expect(fixture.nativeElement.querySelector('iframe')).toBeNull();
    open.mockRestore();
  });

  it('formats JSON and log previews and exposes their copy actions', async () => {
    await TestBed.configureTestingModule({
      imports: [ArtifactGalleryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ArtifactGalleryComponent);
    fixture.componentRef.setInput('artifacts', [
      documentArtifact('json', 'json'),
      documentArtifact('log', 'log'),
    ]);
    await fixture.whenStable();

    (fixture.nativeElement.querySelector('[data-testid="artifact-document-toggle-json"]') as HTMLButtonElement).click();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/files/results/delivery.json')
      .flush('{"pinned":true,"images":8}');
    (fixture.nativeElement.querySelector('[data-testid="artifact-document-toggle-log"]') as HTMLButtonElement).click();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/files/results/delivery.log')
      .flush('capture light: ok\ncapture dark: ok');
    await fixture.whenStable();

    const json = fixture.nativeElement.querySelector('[data-testid="artifact-document-json"]');
    const log = fixture.nativeElement.querySelector('[data-testid="artifact-document-log"]');
    expect(json.textContent).toContain('"pinned": true');
    expect(log.textContent).toContain('capture dark: ok');
    expect(json.querySelector('[data-testid="artifact-document-copy-json"]')).not.toBeNull();
    expect(log.querySelector('[data-testid="artifact-document-copy-log"]')).not.toBeNull();
  });
});
