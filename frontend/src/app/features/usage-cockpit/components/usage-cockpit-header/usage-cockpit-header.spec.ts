import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { beforeEach, describe, expect, it } from 'vitest';
import { StudioNavigationHeaderComponent, type CockpitNavItem } from './usage-cockpit-header';

const ITEMS: readonly CockpitNavItem[] = [
  { id: 'activity', label: 'Activity', inlineFrom: 'phone' },
  { id: 'search', label: 'Search', inlineFrom: 'tablet' },
  { id: 'magazine', label: 'Magazine', inlineFrom: 'desktop' },
  { id: 'settings', label: 'Orchestrator configuration', inlineFrom: null },
];

describe('StudioNavigationHeaderComponent More menu', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] }));

  function menuAt(width: number): string[] {
    const fixture = TestBed.createComponent(StudioNavigationHeaderComponent);
    fixture.componentRef.setInput('navItems', ITEMS);
    Object.defineProperty(fixture.nativeElement, 'clientWidth', { value: width });
    fixture.componentInstance.moreOpen.set(true);
    return fixture.componentInstance.menuItems().map(item => item.id);
  }

  it('carries the rail destinations only below the phone breakpoint (AGT-3836)', () => {
    expect(menuAt(390)).toEqual(['activity', 'search', 'magazine', 'settings']);
    expect(menuAt(600)).toEqual(['activity', 'search', 'magazine', 'settings']);
    expect(menuAt(601)).toEqual(['search', 'magazine', 'settings']);
    expect(menuAt(720)).toEqual(['magazine', 'settings']);
    expect(menuAt(1100)).toEqual(['settings']);
  });
});
