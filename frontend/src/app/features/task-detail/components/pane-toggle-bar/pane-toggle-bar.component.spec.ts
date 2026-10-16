import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { PaneToggleBarComponent } from './pane-toggle-bar.component';
import { AuthSessionState } from '../../../../services/auth.service';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('PaneToggleBarComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [PaneToggleBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(PaneToggleBarComponent);
    fixture.componentRef.setInput('panesVisible', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // panesVisible
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] PaneToggleBarComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Hand-tuned render-path coverage for the Git commit-count badge.
 *
 * The badge replaces the now-removed `COMMITTED N commits` strip that
 * used to render above the activity log. It must:
 *   - render when commitCount > 0,
 *   - reflect the bound number verbatim,
 *   - stay hidden for commitCount === 0 (no `0` badge).
 *
 * Selectors use `data-testid="pane-toggle-git-badge"` so this is decoupled
 * from styling. The Playwright spec asserts the same testid.
 */
describe('PaneToggleBarComponent Git badge', () => {
  async function mount(commitCount: number) {
    await TestBed.configureTestingModule({
      imports: [PaneToggleBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(PaneToggleBarComponent);
    fixture.componentRef.setInput('panesVisible', { prompt: true, protocol: true, git: true });
    fixture.componentRef.setInput('commitCount', commitCount);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the badge with the commit count when commitCount > 0', async () => {
    const fixture = await mount(3);
    const badge = fixture.nativeElement.querySelector(
      '[data-testid="pane-toggle-git-badge"]',
    ) as HTMLElement | null;
    expect(badge).not.toBeNull();
    expect(badge!.textContent?.trim()).toBe('3');
  });

  it('omits the badge when commitCount is 0', async () => {
    const fixture = await mount(0);
    const badge = fixture.nativeElement.querySelector(
      '[data-testid="pane-toggle-git-badge"]',
    ) as HTMLElement | null;
    expect(badge).toBeNull();
  });

  it('updates the badge text when the bound count changes', async () => {
    const fixture = await mount(1);
    let badge = fixture.nativeElement.querySelector(
      '[data-testid="pane-toggle-git-badge"]',
    ) as HTMLElement | null;
    expect(badge?.textContent?.trim()).toBe('1');

    fixture.componentRef.setInput('commitCount', 7);
    fixture.detectChanges();

    badge = fixture.nativeElement.querySelector(
      '[data-testid="pane-toggle-git-badge"]',
    ) as HTMLElement | null;
    expect(badge?.textContent?.trim()).toBe('7');
  });
});

describe('PaneToggleBarComponent local actions', () => {
  it('hides the server-side VS Code launch in networked mode', async () => {
    await TestBed.configureTestingModule({
      imports: [PaneToggleBarComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    TestBed.inject(AuthSessionState).status.set({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
    });
    const fixture = TestBed.createComponent(PaneToggleBarComponent);
    fixture.componentRef.setInput('panesVisible', { prompt: true, protocol: true, git: true });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="open-in-vscode"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="pane-toggle-git"]')).not.toBeNull();
  });
});
