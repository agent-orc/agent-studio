import { InjectionToken } from '@angular/core';
import { PREVIEW_TOKEN_SETS } from './preview-token-sets';

export type ThemeMode = 'light' | 'dark';
export type ThemePreference = ThemeMode | 'system';
export type ThemeTokenValues = Readonly<Record<`--studio-${string}` | `--font-${string}` | `--elevation-${string}` | `--model-family-${string}` | `--lane-${string}`, string>>;

/** A palette family has both modes. Theme preference always chooses the mode. */
export interface ThemeTokenSet {
  readonly id: string;
  readonly label: string;
  readonly description?: string;
  readonly preview?: boolean;
  readonly light: ThemeTokenValues;
  readonly dark: ThemeTokenValues;
}

export interface ResolvedThemeTokenSet {
  readonly id: string;
  readonly setId: string;
  readonly theme: ThemeMode;
  readonly tokens: ThemeTokenValues;
}

export const TOKEN_SET_STORAGE_KEY = 'atp.studio.tokenSet';
export const TOKEN_SET_STORAGE_VERSION = 1;

/** Empty overrides keep the library-owned light and dark sets byte-for-byte. */
export const DEFAULT_THEME_TOKEN_SET: ThemeTokenSet = Object.freeze({
  id: 'studio-default',
  label: 'Studio',
  light: Object.freeze({}),
  dark: Object.freeze({}),
});

/** Optional centrally supplied prototypes, registered before restoring the id. */
export const THEME_TOKEN_SETS = new InjectionToken<readonly ThemeTokenSet[]>('Studio token sets', {
  providedIn: 'root', factory: () => PREVIEW_TOKEN_SETS,
});

/** Snapshot trusted application configuration, never arbitrary stored CSS. */
export function snapshotTokenSet(set: ThemeTokenSet): ThemeTokenSet {
  if (!set || typeof set.id !== 'string' || !/^[a-z][a-z0-9-]{0,63}$/.test(set.id)
    || typeof set.label !== 'string' || !set.label.trim()) {
    throw new Error('Token sets require a stable lowercase id and a label.');
  }
  const snapshot = (tokens: ThemeTokenValues): ThemeTokenValues => {
    if (!tokens || typeof tokens !== 'object' || Array.isArray(tokens)) {
      throw new Error('Token sets require light and dark token maps.');
    }
    for (const [name, value] of Object.entries(tokens)) {
      if (!/^--(?:studio|font|elevation|model-family|lane)-[a-z0-9-]+$/.test(name) || typeof value !== 'string' || !value.trim()) {
        throw new Error('Token set overrides must use central Studio aliases and nonempty CSS values.');
      }
    }
    return Object.freeze({ ...tokens });
  };
  return Object.freeze({ id: set.id, label: set.label, description: set.description, preview: set.preview, light: snapshot(set.light), dark: snapshot(set.dark) });
}

export function restoreTokenSetId(raw: string | null, sets: readonly ThemeTokenSet[]): string {
  try {
    const stored: unknown = JSON.parse(raw ?? 'null');
    if (stored && typeof stored === 'object' && 'version' in stored && 'id' in stored
      && stored.version === TOKEN_SET_STORAGE_VERSION && typeof stored.id === 'string'
      && sets.some(set => set.id === stored.id)) {
      return stored.id;
    }
  } catch { /* Invalid JSON uses the same deterministic default as unknown ids. */ }
  return DEFAULT_THEME_TOKEN_SET.id;
}
