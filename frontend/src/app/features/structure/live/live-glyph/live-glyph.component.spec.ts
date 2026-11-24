import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { LIVE_KIND_LABEL, LIVE_LAYERS } from '../live-structure.model';
import { LiveGlyphComponent } from './live-glyph.component';

describe('LiveGlyphComponent', () => {
  it('draws a different sign for every layer and every kind of node', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const drawn = new Set<string>();
    const names = [...LIVE_LAYERS.map(layer => layer.id), ...Object.keys(LIVE_KIND_LABEL)];
    for (const name of names) {
      const fixture = TestBed.createComponent(LiveGlyphComponent);
      fixture.componentRef.setInput('name', name);
      fixture.detectChanges();
      const svg = (fixture.nativeElement as HTMLElement).querySelector('svg')!;
      expect(svg.getAttribute('aria-hidden')).toBe('true');
      expect(svg.children.length, name).toBeGreaterThan(0);
      drawn.add(svg.innerHTML.replace(/<!--.*?-->/g, ''));
    }
    expect(drawn.size).toBe(names.length);
  });
});
