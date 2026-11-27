import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { StudioSidebarHeaderComponent } from './studio-sidebar-header.component';

describe('StudioSidebarHeaderComponent', () => {
  it('renders the shared sidebar title and subtitle chrome', () => {
    TestBed.configureTestingModule({
      imports: [StudioSidebarHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(StudioSidebarHeaderComponent);
    fixture.componentRef.setInput('title', 'Deck');
    fixture.componentRef.setInput('subtitle', 'Agent Task Processor');
    fixture.componentRef.setInput('testid', 'shared-header');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="shared-header"]')).toBeTruthy();
    expect(host.textContent).toContain('Deck');
    expect(host.textContent).toContain('Agent Task Processor');
  });
});
