import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { Subject, of, throwError } from 'rxjs';
import { CliCatalogStore } from './cli-catalog.store';
import { TaskService } from '../../../services/task.service';
import type { CliModelCatalog, CliModelInfo } from '../models/cli.model';
import type { CliType } from '../../../models/task.model';

interface JobsStub {
  getCliModelCatalog: ReturnType<typeof vi.fn>;
}

function configure(stub: JobsStub): CliCatalogStore {
  TestBed.configureTestingModule({
    providers: [{ provide: TaskService, useValue: stub }],
  });
  return TestBed.inject(CliCatalogStore);
}

const claudeModels: CliModelInfo[] = [
  { id: 'claude-opus-4-7', label: 'Opus 4.7', multiplier: 5, vendor: 'anthropic', isDefault: true },
];

function catalog(models: CliModelInfo[]): CliModelCatalog {
  return { models, source: 'test', fetchedAt: '2026-05-29T00:00:00Z' };
}

describe('CliCatalogStore', () => {
  it('returns empty list when nothing is cached, and reports hasFresh=false', () => {
    const stub: JobsStub = { getCliModelCatalog: vi.fn(() => of(catalog([]))) };
    const store = configure(stub);
    expect(store.modelsFor('claude')).toEqual([]);
    expect(store.hasFresh('claude')).toBe(false);
  });

  it('caches a successful fetch and reads it synchronously on subsequent calls', () => {
    const stub: JobsStub = {
      getCliModelCatalog: vi.fn(() => of(catalog(claudeModels))),
    };
    const store = configure(stub);
    let observed: readonly CliModelInfo[] = [];
    store.ensure('claude').subscribe((m) => (observed = m));
    expect(observed).toEqual(claudeModels);
    expect(store.hasFresh('claude')).toBe(true);
    expect(store.modelsFor('claude')).toEqual(claudeModels);
    expect(stub.getCliModelCatalog).toHaveBeenCalledTimes(1);

    // Re-ensure: no second HTTP call.
    let second: readonly CliModelInfo[] = [];
    store.ensure('claude').subscribe((m) => (second = m));
    expect(second).toEqual(claudeModels);
    expect(stub.getCliModelCatalog).toHaveBeenCalledTimes(1);
  });

  it('caches current generations first while retaining selectable older generations', () => {
    const discovered = [
      { ...claudeModels[0], id: 'claude-opus-4-7', label: 'Opus 4.7', isDefault: false },
      { ...claudeModels[0], id: 'claude-opus-5', label: 'Opus 5', isDefault: true },
      { ...claudeModels[0], id: 'claude-opus-4-8', label: 'Opus 4.8', isDefault: false },
    ];
    const stub: JobsStub = {
      getCliModelCatalog: vi.fn(() => of(catalog(discovered))),
    };
    const store = configure(stub);

    store.ensure('claude').subscribe();

    expect(store.modelsFor('claude').map((item) => item.id)).toEqual([
      'claude-opus-5',
      'claude-opus-4-8',
      'claude-opus-4-7',
    ]);
    expect(store.modelsFor('claude').slice(1)).toEqual([
      expect.objectContaining({ id: 'claude-opus-4-8', olderGeneration: true }),
      expect.objectContaining({ id: 'claude-opus-4-7', olderGeneration: true }),
    ]);
    expect(store.modelsFor('claude').slice(1).every((item) => !item.deprecated)).toBe(true);
    expect(store.modelsFor('claude').every((item) => item.available !== false)).toBe(true);
  });

  it('dedupes concurrent fetches for the same CLI', () => {
    const subj = new Subject<CliModelCatalog>();
    const stub: JobsStub = {
      getCliModelCatalog: vi.fn(() => subj.asObservable()),
    };
    const store = configure(stub);
    const seenA: (readonly CliModelInfo[])[] = [];
    const seenB: (readonly CliModelInfo[])[] = [];
    store.ensure('claude').subscribe((m) => seenA.push(m));
    store.ensure('claude').subscribe((m) => seenB.push(m));
    expect(stub.getCliModelCatalog).toHaveBeenCalledTimes(1);
    subj.next(catalog(claudeModels));
    subj.complete();
    expect(seenA).toEqual([claudeModels]);
    expect(seenB).toEqual([claudeModels]);
  });

  it('refresh forces a re-fetch even when an entry is fresh', () => {
    const stub: JobsStub = {
      getCliModelCatalog: vi.fn(() => of(catalog(claudeModels))),
    };
    const store = configure(stub);
    store.ensure('claude').subscribe();
    expect(stub.getCliModelCatalog).toHaveBeenCalledWith('claude', false);
    store.refresh('claude').subscribe();
    expect(stub.getCliModelCatalog).toHaveBeenCalledWith('claude', true);
    expect(stub.getCliModelCatalog).toHaveBeenCalledTimes(2);
  });

  it('picker-open refresh is forceful but throttled per CLI', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-09T12:00:00Z'));
    try {
      const stub: JobsStub = {
        getCliModelCatalog: vi.fn(() => of(catalog(claudeModels))),
      };
      const store = configure(stub);

      let observed: readonly CliModelInfo[] = [];
      store.refreshForPickerOpen('codex')?.subscribe((m) => (observed = m));
      expect(observed).toEqual(claudeModels);
      expect(stub.getCliModelCatalog).toHaveBeenCalledWith('codex', true);

      expect(store.refreshForPickerOpen('codex')).toBeNull();
      expect(stub.getCliModelCatalog).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(5 * 60 * 1000 + 1);
      store.refreshForPickerOpen('codex')?.subscribe();
      expect(stub.getCliModelCatalog).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('hydrateAll triggers one fetch per CLI type but skips fresh entries on re-call', () => {
    const stub: JobsStub = {
      getCliModelCatalog: vi.fn(() => of(catalog(claudeModels))),
    };
    const store = configure(stub);
    store.hydrateAll();
    // 3 CLI types in CLI_TYPES: claude, codex, gemini.
    expect(stub.getCliModelCatalog).toHaveBeenCalledTimes(3);
    store.hydrateAll();
    expect(stub.getCliModelCatalog).toHaveBeenCalledTimes(3);
  });

  it('invalidate drops the cached entry so the next ensure refetches', () => {
    const stub: JobsStub = {
      getCliModelCatalog: vi.fn(() => of(catalog(claudeModels))),
    };
    const store = configure(stub);
    store.ensure('claude').subscribe();
    expect(store.hasFresh('claude')).toBe(true);
    store.invalidate('claude');
    expect(store.hasFresh('claude')).toBe(false);
    store.ensure('claude').subscribe();
    expect(stub.getCliModelCatalog).toHaveBeenCalledTimes(2);
  });

  it('does not cache a failed fetch and the next ensure retries', () => {
    let attempts = 0;
    const stub: JobsStub = {
      getCliModelCatalog: vi.fn(() => {
        attempts++;
        if (attempts === 1) return throwError(() => new Error('boom'));
        return of(catalog(claudeModels));
      }),
    };
    const store = configure(stub);
    let observedErr: unknown = null;
    store.ensure('claude').subscribe({ error: (e) => (observedErr = e) });
    expect(observedErr).toBeInstanceOf(Error);
    expect(store.hasFresh('claude')).toBe(false);
    let observed: readonly CliModelInfo[] = [];
    store.ensure('claude').subscribe((m) => (observed = m));
    expect(observed).toEqual(claudeModels);
  });

  it('hydrateAll swallows per-CLI errors so one broken CLI does not block the others', () => {
    const broken: CliType = 'gemini';
    const stub: JobsStub = {
      getCliModelCatalog: vi.fn((t: CliType) => {
        if (t === broken) return throwError(() => new Error('not installed'));
        return of(catalog(claudeModels));
      }),
    };
    const store = configure(stub);
    expect(() => store.hydrateAll()).not.toThrow();
    expect(store.hasFresh('claude')).toBe(true);
    expect(store.hasFresh('gemini')).toBe(false);
  });
});
