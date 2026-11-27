import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { MediaLightboxService } from './media-lightbox.service';

describe('MediaLightboxService', () => {
  let service: MediaLightboxService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), MediaLightboxService],
    });
    service = TestBed.inject(MediaLightboxService);
  });

  it('starts with no active media', () => {
    expect(service.active()).toBeNull();
  });

  it('opens with src and alt', () => {
    service.open({ src: '/api/tasks/x/attachments/y.png', alt: 'Screenshot' });
    const active = service.active();
    expect(active).not.toBeNull();
    expect(active?.src).toBe('/api/tasks/x/attachments/y.png');
    expect(active?.alt).toBe('Screenshot');
  });

  it('normalises a missing alt to empty string', () => {
    service.open({ src: '/x.png' });
    expect(service.active()?.alt).toBe('');
  });

  it('ignores a blank src so a misfire never opens a black overlay', () => {
    service.open({ src: '   ', alt: 'noop' });
    expect(service.active()).toBeNull();
  });

  it('close() clears the active media', () => {
    service.open({ src: '/x.png', alt: 'y' });
    expect(service.active()).not.toBeNull();
    service.close();
    expect(service.active()).toBeNull();
  });

  it('open() reports a single-image gallery', () => {
    service.open({ src: '/x.png', alt: 'y' });
    expect(service.count()).toBe(1);
    expect(service.position()).toBe(1);
    expect(service.hasPrev()).toBe(false);
    expect(service.hasNext()).toBe(false);
  });

  describe('openGallery', () => {
    const images = [
      { src: '/a.png', alt: 'A' },
      { src: '/b.png', alt: 'B' },
      { src: '/c.png', alt: 'C' },
    ];

    it('opens at the requested index and exposes position/count', () => {
      service.openGallery({ images, index: 1 });
      expect(service.count()).toBe(3);
      expect(service.position()).toBe(2);
      expect(service.active()?.src).toBe('/b.png');
      expect(service.hasPrev()).toBe(true);
      expect(service.hasNext()).toBe(true);
    });

    it('defaults to the first image when no index is given', () => {
      service.openGallery({ images });
      expect(service.position()).toBe(1);
      expect(service.active()?.src).toBe('/a.png');
    });

    it('next()/prev() page through the gallery', () => {
      service.openGallery({ images, index: 0 });
      service.next();
      expect(service.active()?.src).toBe('/b.png');
      service.next();
      expect(service.active()?.src).toBe('/c.png');
      service.prev();
      expect(service.active()?.src).toBe('/b.png');
    });

    it('clamps at the end - next() on the last image is a no-op', () => {
      service.openGallery({ images, index: 2 });
      expect(service.hasNext()).toBe(false);
      service.next();
      expect(service.position()).toBe(3);
      expect(service.active()?.src).toBe('/c.png');
    });

    it('clamps at the start - prev() on the first image is a no-op', () => {
      service.openGallery({ images, index: 0 });
      expect(service.hasPrev()).toBe(false);
      service.prev();
      expect(service.position()).toBe(1);
      expect(service.active()?.src).toBe('/a.png');
    });

    it('clamps an out-of-range requested index into the gallery', () => {
      service.openGallery({ images, index: 99 });
      expect(service.position()).toBe(3);
      service.openGallery({ images, index: -5 });
      expect(service.position()).toBe(1);
    });

    it('drops blank-src entries and re-indexes around them', () => {
      service.openGallery({
        images: [{ src: '/a.png' }, { src: '   ' }, { src: '/c.png' }],
        index: 2,
      });
      expect(service.count()).toBe(2);
      // The blank middle entry is gone, so the clicked third image (index 2)
      // clamps onto the now-last real image.
      expect(service.active()?.src).toBe('/c.png');
    });

    it('ignores an all-blank gallery so a misfire never opens an overlay', () => {
      service.openGallery({ images: [{ src: '' }, { src: '  ' }] });
      expect(service.active()).toBeNull();
      expect(service.count()).toBe(0);
    });
  });
});
