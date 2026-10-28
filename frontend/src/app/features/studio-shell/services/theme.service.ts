import { DestroyRef, Injectable, computed, effect, inject, signal } from '@angular/core';

const THEME_KEY = 'atp.studio.theme';

/**
 * AGT-2035 — single source of truth for the app theme.
 *
 * Theme used to be a local signal on `StudioShellComponent`; the settings
 * consolidation makes Theme a *global* preference surfaced in the Appearance
 * section of the one Workspace-settings view, while the titlebar keeps its
 * moon/sun quick-toggle. Both now read and write this one service instead of
 * each owning a copy, so they can never drift.
 *
 * The service reflects the current theme onto `document.documentElement`
 * (`data-studio-theme`) so the design tokens flip, and persists the choice to
 * localStorage. Default is Light to preserve existing preferences; System
 * follows the operating-system colour scheme when selected.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly destroyRef = inject(DestroyRef);
  private readonly mediaQuery = typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  private readonly systemDark = signal(this.mediaQuery?.matches ?? false);
  private readonly _preference = signal<'system' | 'dark' | 'light'>(this.savedPreference());
  readonly preference = this._preference.asReadonly();

  /** Resolved theme used by the titlebar and design tokens. */
  readonly theme = computed<'dark' | 'light'>(() => {
    const preference = this._preference();
    return preference === 'system' ? (this.systemDark() ? 'dark' : 'light') : preference;
  });

  constructor() {
    const onSystemChange = (event: MediaQueryListEvent) => this.systemDark.set(event.matches);
    this.mediaQuery?.addEventListener('change', onSystemChange);
    this.destroyRef.onDestroy(() => this.mediaQuery?.removeEventListener('change', onSystemChange));
    // Reflect the theme onto the document root + persist on every change.
    effect(() => {
      document.documentElement.dataset['studioTheme'] = this.theme();
      try { localStorage.setItem(THEME_KEY, this._preference()); } catch { /* storage blocked */ }
    });
  }

  private savedPreference(): 'system' | 'dark' | 'light' {
    try {
      const value = localStorage.getItem(THEME_KEY);
      return value === 'system' || value === 'dark' || value === 'light' ? value : 'light';
    } catch {
      return 'light';
    }
  }

  set(value: 'system' | 'dark' | 'light'): void {
    this._preference.set(value);
  }

  toggle(): void {
    this._preference.set(this.theme() === 'dark' ? 'light' : 'dark');
  }
}
