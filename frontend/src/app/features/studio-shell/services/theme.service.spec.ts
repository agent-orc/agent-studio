import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeService } from './theme.service';
import {
  DEFAULT_THEME_TOKEN_SET,
  THEME_TOKEN_SETS,
  TOKEN_SET_STORAGE_KEY,
  type ThemeTokenSet,
} from './theme-token-sets';

// A partial runtime registration exercises cleanup independently of the bundled previews.
const prototype: ThemeTokenSet = {
  id: 'test-prototype', label: 'Test prototype',
  light: { '--studio-accent': 'var(--studio-fg)' },
  dark: { '--studio-accent': 'var(--studio-fg-strong)' },
};

describe('ThemeService', () => {
  let listener: ((event: MediaQueryListEvent) => void) | undefined;
  const removeListener = vi.fn();

  const flush = () => TestBed.tick();
  const systemChange = (matches: boolean) => {
    listener?.({ matches } as MediaQueryListEvent);
    flush();
  };

  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem('atp.studio.theme');
    localStorage.removeItem(TOKEN_SET_STORAGE_KEY);
    removeListener.mockClear();
    listener = undefined;
    history.replaceState(null, '', '/');
    vi.stubGlobal('matchMedia', vi.fn(() => ({
      matches: false,
      addEventListener: (_: string, callback: typeof listener) => { listener = callback; },
      removeEventListener: removeListener,
    })));
  });

  afterEach(() => {
    history.replaceState(null, '', '/');
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    localStorage.removeItem('atp.studio.theme');
    localStorage.removeItem(TOKEN_SET_STORAGE_KEY);
    document.documentElement.style.removeProperty('--studio-accent');
    document.documentElement.style.removeProperty('--studio-fg');
    delete document.documentElement.dataset['studioTheme'];
    delete document.documentElement.dataset['studioTokenSet'];
  });

  it.each(['light', 'dark', 'system'] as const)('restores and persists the %s preference independently of the set', preference => {
    localStorage.setItem('atp.studio.theme', preference);
    let service = TestBed.inject(ThemeService);
    flush();
    expect(service.preference()).toBe(preference);
    expect(service.theme()).toBe(preference === 'system' ? 'light' : preference);
    expect(service.tokenSetId()).toBe(DEFAULT_THEME_TOKEN_SET.id);
    expect(document.documentElement.dataset['studioTokenSet']).toBe(`studio-default.${service.theme()}`);
    service.set(preference === 'light' ? 'dark' : 'light');
    flush();
    const saved = service.preference();
    TestBed.resetTestingModule();
    service = TestBed.inject(ThemeService);
    flush();
    expect(service.preference()).toBe(saved);
    expect(localStorage.getItem('atp.studio.theme')).toBe(saved);
    expect(JSON.parse(localStorage.getItem(TOKEN_SET_STORAGE_KEY)!)).toEqual({ version: 1, id: 'studio-default' });
  });

  it.each([null, '', 'null', '{', '"studio-default"', '{"version":1,"id":"unknown"}',
    '{"version":2,"id":"studio-default"}', '{"version":1,"id":42}',
    '{"version":1,"id":"constructor"}'])('repairs invalid persisted set %s deterministically', saved => {
    if (saved !== null) localStorage.setItem(TOKEN_SET_STORAGE_KEY, saved);
    localStorage.setItem('atp.studio.theme', 'dark');
    const service = TestBed.inject(ThemeService);
    flush();
    expect(service.tokenSetId()).toBe('studio-default');
    expect(service.preference()).toBe('dark');
    expect(document.documentElement.dataset['studioTheme']).toBe('dark');
    expect(JSON.parse(localStorage.getItem(TOKEN_SET_STORAGE_KEY)!)).toEqual({ version: 1, id: 'studio-default' });
  });

  it('switches runtime sets centrally, resolves both variants and removes old overrides', () => {
    const root = document.documentElement;
    root.style.setProperty('--studio-accent', 'var(--studio-fg-dim)', 'important');
    const service = TestBed.inject(ThemeService);
    service.registerTokenSet(prototype);
    service.setTokenSet(prototype.id);
    flush();
    expect(service.preference()).toBe('light');
    expect(service.tokenSet().id).toBe('test-prototype.light');
    expect(root.style.getPropertyValue('--studio-accent')).toBe(prototype.light['--studio-accent']);
    expect(JSON.parse(localStorage.getItem(TOKEN_SET_STORAGE_KEY)!)).toEqual({ version: 1, id: prototype.id });
    service.set('dark');
    flush();
    expect(root.style.getPropertyValue('--studio-accent')).toBe(prototype.dark['--studio-accent']);
    service.setTokenSet('unknown');
    flush();
    expect(service.tokenSetId()).toBe('studio-default');
    expect(root.style.getPropertyValue('--studio-accent')).toBe('var(--studio-fg-dim)');
    expect(root.style.getPropertyPriority('--studio-accent')).toBe('important');
    root.style.removeProperty('--studio-accent');
  });

  it('restores a registered set on reload and snapshots caller-owned token maps', () => {
    const light = { '--studio-accent': 'var(--studio-fg)' };
    TestBed.configureTestingModule({ providers: [{ provide: THEME_TOKEN_SETS, useValue: [{ ...prototype, light }] }] });
    localStorage.setItem(TOKEN_SET_STORAGE_KEY, JSON.stringify({ version: 1, id: prototype.id }));
    const service = TestBed.inject(ThemeService);
    light['--studio-accent'] = 'var(--studio-fg-muted)';
    flush();
    expect(service.tokenSetId()).toBe(prototype.id);
    expect(service.tokenSet().tokens['--studio-accent']).toBe('var(--studio-fg)');
    expect(localStorage.getItem('atp.studio.theme')).toBe('light');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: THEME_TOKEN_SETS, useValue: [prototype] }] });
    const restored = TestBed.inject(ThemeService);
    flush();
    expect(restored.tokenSetId()).toBe(prototype.id);
    expect(restored.tokenSet().tokens['--studio-accent']).toBe('var(--studio-fg)');
  });

  it('removes partial overrides on mode changes and service destruction', () => {
    const service = TestBed.inject(ThemeService);
    service.registerTokenSet({ ...prototype, light: { '--studio-fg': 'var(--studio-accent)' } });
    service.setTokenSet(prototype.id);
    flush();
    expect(document.documentElement.style.getPropertyValue('--studio-fg')).toBe('var(--studio-accent)');
    service.set('dark');
    flush();
    expect(document.documentElement.style.getPropertyValue('--studio-fg')).toBe('');
    expect(document.documentElement.style.getPropertyValue('--studio-accent')).toBe(prototype.dark['--studio-accent']);
    TestBed.resetTestingModule();
    expect(document.documentElement.style.getPropertyValue('--studio-accent')).toBe('');
  });

  it('keeps System selected across OS changes, persists it, and removes the listener', () => {
    const service = TestBed.inject(ThemeService);
    service.registerTokenSet(prototype);
    service.setTokenSet(prototype.id);
    service.set('system');
    systemChange(true);
    expect(service.tokenSet().id).toBe('test-prototype.dark');
    systemChange(false);
    expect(service.tokenSet().id).toBe('test-prototype.light');
    expect(localStorage.getItem('atp.studio.theme')).toBe('system');
    expect(service.tokenSetId()).toBe(prototype.id);
    TestBed.resetTestingModule();
    expect(removeListener).toHaveBeenCalledWith('change', listener);
  });

  it('ignores OS changes for explicit themes and keeps quick toggle behaviour', () => {
    const service = TestBed.inject(ThemeService);
    service.set('light');
    systemChange(true);
    expect(service.theme()).toBe('light');
    service.set('system');
    flush();
    expect(service.theme()).toBe('dark');
    service.toggle();
    flush();
    expect(service.preference()).toBe('light');
  });

  it('falls back when storage is blocked or matchMedia is unavailable', () => {
    vi.stubGlobal('matchMedia', undefined);
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('Storage blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage blocked'); });
    const service = TestBed.inject(ThemeService);
    service.set('system');
    flush();
    expect(service.theme()).toBe('light');
    expect(service.tokenSetId()).toBe('studio-default');
  });

  it('rejects malformed registrations and duplicate ids without changing the active set', () => {
    const service = TestBed.inject(ThemeService);
    expect(() => service.registerTokenSet({ ...prototype, id: 'Bad id' })).toThrow();
    expect(() => service.registerTokenSet({ ...prototype, id: undefined } as unknown as ThemeTokenSet)).toThrow();
    expect(() => service.registerTokenSet({ ...prototype, dark: undefined } as unknown as ThemeTokenSet)).toThrow();
    expect(() => service.registerTokenSet({ ...prototype, light: { '--color-orange-500': 'inherit' } } as unknown as ThemeTokenSet)).toThrow();
    expect(() => service.registerTokenSet(DEFAULT_THEME_TOKEN_SET)).toThrow();
    expect(service.tokenSetId()).toBe('studio-default');
  });
  it('uses a query preview for this visit without replacing the saved browser choice', () => {
    localStorage.setItem(TOKEN_SET_STORAGE_KEY, JSON.stringify({ version: 1, id: 'preview-1' }));
    history.replaceState(null, '', '/?tokenSet=preview-3#/board');
    const service = TestBed.inject(ThemeService);
    flush();
    expect(service.tokenSetId()).toBe('preview-3');
    service.set('dark');
    flush();
    expect(service.tokenSet().id).toBe('preview-3.dark');
    expect(JSON.parse(localStorage.getItem(TOKEN_SET_STORAGE_KEY)!).id).toBe('preview-1');
    history.replaceState(null, '', '/#/board');
    TestBed.resetTestingModule();
    expect(TestBed.inject(ThemeService).tokenSetId()).toBe('preview-1');
  });

  it('lets an explicit settings choice replace a visit override and ignores unknown query ids', () => {
    history.replaceState(null, '', '/?tokenSet=preview-3&keep=1#/board');
    const service = TestBed.inject(ThemeService);
    service.setTokenSet('preview-2');
    flush();
    expect(service.tokenSetId()).toBe('preview-2');
    expect(location.search).toBe('?keep=1');
    expect(location.hash).toBe('#/board');
    expect(JSON.parse(localStorage.getItem(TOKEN_SET_STORAGE_KEY)!).id).toBe('preview-2');
    TestBed.resetTestingModule();
    history.replaceState(null, '', '/?tokenSet=unknown');
    expect(TestBed.inject(ThemeService).tokenSetId()).toBe('preview-2');
  });

});
