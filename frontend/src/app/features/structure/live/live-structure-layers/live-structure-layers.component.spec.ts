import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { LiveLayer } from '../live-structure.model';
import { LiveStructureLayersComponent } from './live-structure-layers.component';

describe('LiveStructureLayersComponent', () => {
  it('shows five labelled switches with their state in words and emits the layer that was pressed', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(LiveStructureLayersComponent);
    fixture.componentRef.setInput('layers', { perf: true, network: false, storage: false, backup: true, warnings: false });
    fixture.componentRef.setInput('summaries', { perf: '5 of 12 slots busy', network: '7 routes', storage: 'fullest disk 84%', backup: 'not observed', warnings: '8 open' });
    const pressed: LiveLayer[] = [];
    fixture.componentInstance.layerToggle.subscribe(layer => pressed.push(layer));
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const switches = [...root.querySelectorAll<HTMLButtonElement>('button[role="switch"]')];
    expect(switches.map(item => item.dataset['layer'])).toEqual(['perf', 'network', 'storage', 'backup', 'warnings']);
    expect(switches.map(item => item.getAttribute('aria-checked'))).toEqual(['true', 'false', 'false', 'true', 'false']);
    expect(switches.map(item => item.querySelector('.layers__word')!.textContent)).toEqual(['On', 'Off', 'Off', 'On', 'Off']);
    expect(switches[0].textContent).toContain('Performance');
    expect(switches[3].textContent).toContain('not observed');
    // One sign per layer, so the layers differ without colour.
    expect(new Set(switches.map(item => item.querySelector('svg')!.getAttribute('data-glyph'))).size).toBe(5);
    switches[2].click();
    expect(pressed).toEqual(['storage']);
  });
});
