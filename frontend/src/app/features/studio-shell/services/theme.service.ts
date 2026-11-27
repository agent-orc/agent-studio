import { DestroyRef, Injectable, computed, effect, inject, signal } from '@angular/core';
import {
  DEFAULT_THEME_TOKEN_SET, THEME_TOKEN_SETS, TOKEN_SET_STORAGE_KEY, TOKEN_SET_STORAGE_VERSION,
  restoreTokenSetId, snapshotTokenSet,
  type ResolvedThemeTokenSet, type ThemeMode, type ThemePreference, type ThemeTokenSet,
} from './theme-token-sets';

const THEME_KEY = 'atp.studio.theme';

/**
 * Single source of truth for theme preference and the independent token set.
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
  private readonly _tokenSets = signal<readonly ThemeTokenSet[]>([DEFAULT_THEME_TOKEN_SET]);
  readonly tokenSets = this._tokenSets.asReadonly();
  private readonly suppliedTokenSets = inject(THEME_TOKEN_SETS);
  private readonly mediaQuery = typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  private readonly systemDark = signal(this.mediaQuery?.matches ?? false);
  private readonly _preference = signal<ThemePreference>(this.savedPreference());
  readonly preference = this._preference.asReadonly();
  private readonly _tokenSetId = signal(DEFAULT_THEME_TOKEN_SET.id);
  private readonly visitTokenSetId = signal<string | null>(null);
  readonly tokenSetId = computed(() => this.visitTokenSetId() ?? this._tokenSetId());

  /** Resolved theme used by the titlebar and design tokens. */
  readonly theme = computed<ThemeMode>(() => {
    const preference = this._preference();
    return preference === 'system' ? (this.systemDark() ? 'dark' : 'light') : preference;
  });

  readonly tokenSet = computed<ResolvedThemeTokenSet>(() => {
    const set = this._tokenSets().find(set => set.id === this.tokenSetId()) ?? DEFAULT_THEME_TOKEN_SET;
    const theme = this.theme();
    return { id: `${set.id}.${theme}`, setId: set.id, theme, tokens: set[theme] };
  });

  constructor() {
    for (const set of this.suppliedTokenSets) this.registerTokenSet(set);
    try {
      this._tokenSetId.set(restoreTokenSetId(localStorage.getItem(TOKEN_SET_STORAGE_KEY), this._tokenSets()));
    } catch { /* Storage blocked: keep the default set. */ }
    const visitId = new URLSearchParams(window.location.search).get('tokenSet');
    if (this._tokenSets().some(set => set.id === visitId)) this.visitTokenSetId.set(visitId);
    const onSystemChange = (event: MediaQueryListEvent) => this.systemDark.set(event.matches);
    this.mediaQuery?.addEventListener('change', onSystemChange);
    this.destroyRef.onDestroy(() => this.mediaQuery?.removeEventListener('change', onSystemChange));
    // Overrides live only at the root. Restore prior inline values when a set
    // changes, so partial prototypes cannot leak into the next set or mode.
    effect(onCleanup => {
      const root = document.documentElement;
      const resolved = this.tokenSet();
      root.dataset['studioTheme'] = resolved.theme;
      root.dataset['studioTokenSet'] = resolved.id;
      const previous = Object.keys(resolved.tokens).map(name => ({
        name, value: root.style.getPropertyValue(name), priority: root.style.getPropertyPriority(name),
      }));
      for (const [name, value] of Object.entries(resolved.tokens)) root.style.setProperty(name, value);
      onCleanup(() => {
        for (const { name, value, priority } of previous) {
          if (value) root.style.setProperty(name, value, priority);
          else root.style.removeProperty(name);
        }
      });
      try { localStorage.setItem(THEME_KEY, this._preference()); } catch { /* storage blocked */ }
      try {
        localStorage.setItem(TOKEN_SET_STORAGE_KEY, JSON.stringify({ version: TOKEN_SET_STORAGE_VERSION, id: this._tokenSetId() }));
      } catch { /* storage blocked */ }
    });
  }

  /** Register a trusted prototype at runtime, then select it without a build. */
  registerTokenSet(set: ThemeTokenSet): void {
    const snapshot = snapshotTokenSet(set);
    if (this._tokenSets().some(existing => existing.id === snapshot.id)) {
      throw new Error(`Token set is already registered: ${snapshot.id}`);
    }
    this._tokenSets.update(sets => [...sets, snapshot]);
  }

  setTokenSet(id: string): void {
    this.visitTokenSetId.set(null);
    const url = new URL(window.location.href);
    if (url.searchParams.has('tokenSet')) {
      url.searchParams.delete('tokenSet');
      window.history.replaceState(window.history.state, '', url);
    }
    this._tokenSetId.set(this._tokenSets().some(set => set.id === id) ? id : DEFAULT_THEME_TOKEN_SET.id);
  }

  private savedPreference(): ThemePreference {
    try {
      const value = localStorage.getItem(THEME_KEY);
      return value === 'system' || value === 'dark' || value === 'light' ? value : 'light';
    } catch {
      return 'light';
    }
  }

  set(value: ThemePreference): void {
    this._preference.set(value);
  }

  toggle(): void {
    this._preference.set(this.theme() === 'dark' ? 'light' : 'dark');
  }
}
