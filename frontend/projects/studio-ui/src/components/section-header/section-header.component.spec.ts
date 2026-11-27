import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { SectionHeaderComponent } from './section-header.component';

/**
 * Collapsible section-header contract (moved here from the former
 * app-pane-header section mode):
 *
 * - `collapsible=true` renders the header as a button with a leading
 *   chevron and the `.section-header` chrome.
 * - Clicking the button emits `collapsedChange` with the FLIPPED
 *   collapsed state so the parent can update its persisted map.
 * - `aria-expanded` mirrors the `collapsed` input — the contract the
 *   F27/F46 explorer collapse specs assert on.
 */
describe('SectionHeaderComponent', () => {
  async function mount(setup?: (ref: ReturnType<typeof TestBed.createComponent<SectionHeaderComponent>>) => void) {
    await TestBed.configureTestingModule({
      imports: [SectionHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(SectionHeaderComponent);
    fixture.componentRef.setInput('title', 'Workspaces');
    setup?.(fixture);
    fixture.detectChanges();
    return fixture;
  }

  it('renders a static heading div by default (not a button)', async () => {
    const fixture = await mount();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('div.section-header--static')).toBeTruthy();
    expect(root.querySelector('button')).toBeNull();
  });

  it('renders as a button with a chev when collapsible', async () => {
    const fixture = await mount((f) => f.componentRef.setInput('collapsible', true));
    const root: HTMLElement = fixture.nativeElement;
    const btn = root.querySelector('button.section-header--collapsible');
    expect(btn).toBeTruthy();
    expect(root.querySelector('.section-header__chev')).toBeTruthy();
    expect(btn?.getAttribute('aria-expanded')).toBe('true');
  });

  it('emits collapsedChange = true when expanded and the user clicks', async () => {
    const fixture = await mount((f) => f.componentRef.setInput('collapsible', true));
    const emitted: boolean[] = [];
    fixture.componentInstance.collapsedChange.subscribe((v: boolean) => emitted.push(v));
    (fixture.nativeElement.querySelector('button.section-header--collapsible') as HTMLButtonElement).click();
    expect(emitted).toEqual([true]);
  });

  it('emits collapsedChange = false when collapsed and the user clicks', async () => {
    const fixture = await mount((f) => {
      f.componentRef.setInput('collapsible', true);
      f.componentRef.setInput('collapsed', true);
    });
    const emitted: boolean[] = [];
    fixture.componentInstance.collapsedChange.subscribe((v: boolean) => emitted.push(v));
    const btn = fixture.nativeElement.querySelector('button.section-header--collapsible') as HTMLButtonElement;
    expect(btn.getAttribute('aria-expanded')).toBe('false');
    btn.click();
    expect(emitted).toEqual([false]);
  });

  it('passes the testid through and shows the count pill', async () => {
    const fixture = await mount((f) => {
      f.componentRef.setInput('testid', 'studio-explorer-workspace-head');
      f.componentRef.setInput('count', 7);
    });
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('[data-testid="studio-explorer-workspace-head"]')).toBeTruthy();
    expect(root.querySelector('.count-badge')?.textContent?.trim()).toBe('7');
  });

  it('adds the divider modifier when divider=true', async () => {
    const fixture = await mount((f) => f.componentRef.setInput('divider', true));
    expect(fixture.nativeElement.querySelector('.section-header--divider')).toBeTruthy();
  });
});
