import { ChangeDetectionStrategy, Component, ViewEncapsulation } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, onTestFinished } from 'vitest';
import { PREVIEW_TOKEN_SETS } from './preview-token-sets';
import { DEFAULT_THEME_TOKEN_SET, snapshotTokenSet, type ThemeTokenValues } from './theme-token-sets';

// The unit target omits global styles. Load the default palette for this contract.
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  // eslint-disable-next-line @angular-eslint/component-max-inline-declarations -- The fixture loads only the palette.
  template: '',
  styleUrl: '../../../../styles/_tokens-semantic.scss',
  encapsulation: ViewEncapsulation.None,
})
class TokenPaletteComponent {}

function resolve(tokens: ThemeTokenValues, name: string): string {
  const value = tokens[name as keyof ThemeTokenValues];
  if (!value) throw new Error(`Missing preview role: ${name}`);
  const alias = /^var\((--[a-z0-9-]+)\)$/.exec(value);
  if (alias) return resolve(tokens, alias[1]);
  const mix = /^color-mix\(in srgb, var\((--[a-z0-9-]+)\) (\d+)%, var\((--[a-z0-9-]+)\)\)$/.exec(value);
  if (mix) {
    const first = resolve(tokens, mix[1]);
    const second = resolve(tokens, mix[3]);
    const weight = Number(mix[2]) / 100;
    return '#' + [1, 3, 5].map(index => Math.round(
      parseInt(first.slice(index, index + 2), 16) * weight + parseInt(second.slice(index, index + 2), 16) * (1 - weight),
    ).toString(16).padStart(2, '0')).join('');
  }
  return value;
}
function luminance(hex: string): number {
  const linear = [1, 3, 5].map(index => {
    const channel = parseInt(hex.slice(index, index + 2), 16) / 255;
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  });
  return linear[0] * 0.2126 + linear[1] * 0.7152 + linear[2] * 0.0722;
}
const surfaces = ['bg-surface', 'bg-editor', 'bg-sidebar', 'bg-elevated', 'bg-hover', 'bg-selected',
  'surface-muted', 'card-bg', 'nav-active-bg'];
const inks = ['fg', 'fg-strong', 'fg-dim', 'fg-muted', 'accent-strong', 'nav-active-fg', 'status-success',
  'status-warning', 'status-danger', 'status-running', 'status-info', 'accent-4-strong'];

describe('Studio token sets', () => {
  it.each(['light', 'dark'] as const)('resolves the danger role to a colour in every %s palette', theme => {
    TestBed.createComponent(TokenPaletteComponent);
    const root = document.documentElement;
    const previous = root.dataset['studioTheme'];
    onTestFinished(() => {
      if (previous === undefined) delete root.dataset['studioTheme'];
      else root.dataset['studioTheme'] = previous;
    });
    root.dataset['studioTheme'] = theme;
    const style = getComputedStyle(root);
    const defaults = Object.fromEntries(Array.from(style, name => [name, style.getPropertyValue(name).trim()]));
    for (const set of [DEFAULT_THEME_TOKEN_SET, ...PREVIEW_TOKEN_SETS]) {
      const danger = resolve({ ...defaults, ...set[theme] }, '--studio-status-danger');
      expect(danger, set.id).toMatch(/^#[0-9a-f]{6}$/i);
    }
  });

  for (const set of PREVIEW_TOKEN_SETS) {
    it(`${set.id} registers both themes and keeps every text/surface combination AA`, () => {
      expect(snapshotTokenSet(set).preview).toBe(true);
      for (const theme of ['light', 'dark'] as const) {
        const tokens = set[theme];
        const ratio = (fg: string, bg: string) => {
          const values = [luminance(resolve(tokens, fg)), luminance(resolve(tokens, bg))].sort((a, b) => a - b);
          return (values[1] + 0.05) / (values[0] + 0.05);
        };
        for (const surface of surfaces) {
          for (const ink of inks) {
            expect(ratio(`--studio-${ink}`, `--studio-${surface}`), `${theme}: ${ink}/${surface}`).toBeGreaterThanOrEqual(4.5);
          }
          for (const family of ['codex', 'claude', 'gemini']) {
            expect(ratio(`--model-family-${family}`, `--studio-${surface}`), `${theme}: ${family}/${surface}`).toBeGreaterThanOrEqual(4.5);
          }
        }
        expect(ratio('--studio-on-accent', '--studio-accent')).toBeGreaterThanOrEqual(4.5);
        expect(ratio('--studio-tooltip-fg', '--studio-tooltip-bg')).toBeGreaterThanOrEqual(4.5);
      }
    });
  }
});
