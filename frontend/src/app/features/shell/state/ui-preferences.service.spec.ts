import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { UiPreferencesService } from './ui-preferences.service';

/**
 * F24 regression. Two pieces locked here:
 *
 *  - the constructor reads the persisted keys from localStorage so a
 *    reload lands on the last-saved posture.
 *  - the `storage` event - the only signal the browser gives a tab
 *    that a SIBLING tab wrote localStorage - mirrors that write back
 *    into the local signals so two open tabs converge on the same
 *    posture without a page reload.
 *
 * The browser only fires `storage` in OTHER tabs, never in the writing
 * tab; we simulate that "other tab" by dispatching a synthetic
 * StorageEvent against window. The service mutates the signal; the
 * test reads it back.
 */
describe('UiPreferencesService', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    localStorage.clear();
    delete document.documentElement.dataset['studioDensity'];
  });

  const densityKey = 'atp.studio.surfaceDensity.v1';

  it('defaults surface density to compact independently of retired card density', () => {
    localStorage.setItem('compactCards', '0');
    const service = TestBed.inject(UiPreferencesService);
    expect(service.surfaceDensity()).toBe('compact');
    expect(document.documentElement.dataset['studioDensity']).toBe('compact');
    expect(localStorage.getItem('compactCards')).toBeNull();
  });

  it.each(['compact', 'default', 'cozy'] as const)('persists and restores %s surface density', density => {
    let service = TestBed.inject(UiPreferencesService);
    service.setSurfaceDensity(density);
    expect(service.surfaceDensity()).toBe(density);
    expect(document.documentElement.dataset['studioDensity']).toBe(density);
    expect(localStorage.getItem(densityKey)).toBe(density);
    TestBed.resetTestingModule();
    service = TestBed.inject(UiPreferencesService);
    expect(service.surfaceDensity()).toBe(density);
    expect(document.documentElement.dataset['studioDensity']).toBe(density);
  });

  it.each([null, '', 'invalid', 'COMPACT'])('falls back to compact for stored density %s', value => {
    if (value !== null) localStorage.setItem(densityKey, value);
    const service = TestBed.inject(UiPreferencesService);
    expect(service.surfaceDensity()).toBe('compact');
    expect(document.documentElement.dataset['studioDensity']).toBe('compact');
  });

  it('mirrors sibling density writes, removals and storage clear without echoing writes', () => {
    const service = TestBed.inject(UiPreferencesService);
    const write = vi.spyOn(Storage.prototype, 'setItem');
    for (const value of ['default', 'cozy', 'invalid', null]) {
      window.dispatchEvent(new StorageEvent('storage', {
        key: densityKey, newValue: value, storageArea: localStorage,
      }));
      const expected = value === 'default' || value === 'cozy' ? value : 'compact';
      expect(service.surfaceDensity()).toBe(expected);
      expect(document.documentElement.dataset['studioDensity']).toBe(expected);
    }
    window.dispatchEvent(new StorageEvent('storage', {
      key: densityKey, newValue: 'cozy', storageArea: localStorage,
    }));
    window.dispatchEvent(new StorageEvent('storage', { key: null, storageArea: localStorage }));
    expect(service.surfaceDensity()).toBe('compact');
    expect(document.documentElement.dataset['studioDensity']).toBe('compact');
    expect(write).not.toHaveBeenCalled();
  });

  it('ignores density writes from session storage and removes the listener on destruction', () => {
    const service = TestBed.inject(UiPreferencesService);
    window.dispatchEvent(new StorageEvent('storage', {
      key: densityKey, newValue: 'cozy', storageArea: sessionStorage,
    }));
    expect(service.surfaceDensity()).toBe('compact');
    TestBed.resetTestingModule();
    window.dispatchEvent(new StorageEvent('storage', {
      key: densityKey, newValue: 'cozy', storageArea: localStorage,
    }));
    expect(service.surfaceDensity()).toBe('compact');
  });

  it('keeps density usable when persistence fails', () => {
    const service = TestBed.inject(UiPreferencesService);
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage blocked'); });
    expect(() => service.setSurfaceDensity('cozy')).not.toThrow();
    expect(service.surfaceDensity()).toBe('cozy');
    expect(document.documentElement.dataset['studioDensity']).toBe('cozy');
  });

  it('AGT-2035 migration: clears the abolished compactCards key on construction', () => {
    localStorage.setItem('compactCards', '1');
    TestBed.inject(UiPreferencesService);
    expect(localStorage.getItem('compactCards')).toBeNull();
  });

  it('defaults tree metrics to numbers and persists the experimental dot view', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.treeMetricView()).toBe('numbers');
    svc.setTreeMetricView('dots');
    expect(svc.treeMetricView()).toBe('dots');
    expect(localStorage.getItem('atp.studio.explorer.metrics')).toBe('dots');
  });

  it('opens Project Chat on project entry by default and persists an opt-out', () => {
    const svc = TestBed.inject(UiPreferencesService);

    expect(svc.openProjectChatOnEntry()).toBe(true);
    svc.setOpenProjectChatOnEntry(false);

    expect(svc.openProjectChatOnEntry()).toBe(false);
    expect(localStorage.getItem('atp.studio.openProjectChatOnEntry.v1')).toBe('0');
  });

  it('restores and mirrors the Project Chat entry preference across tabs', () => {
    localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0');
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.openProjectChatOnEntry()).toBe(false);

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'atp.studio.openProjectChatOnEntry.v1',
        newValue: '1',
        storageArea: localStorage,
      }),
    );
    expect(svc.openProjectChatOnEntry()).toBe(true);
  });

  it('mirrors a sibling tab tree metric write via the storage event', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.treeMetricView()).toBe('numbers');

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'atp.studio.explorer.metrics',
        newValue: 'dots',
        storageArea: localStorage,
      }),
    );
    expect(svc.treeMetricView()).toBe('dots');

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'atp.studio.explorer.metrics',
        newValue: 'numbers',
        storageArea: localStorage,
      }),
    );
    expect(svc.treeMetricView()).toBe('numbers');
  });

  it('mirrors a sibling tab taskNavCollapsed write via the storage event', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.taskNavCollapsed()).toBe(false);

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'taskNavCollapsed',
        newValue: '1',
        storageArea: localStorage,
      }),
    );
    expect(svc.taskNavCollapsed()).toBe(true);
  });

  it('mirrors a sibling tab sideSheetWidth write via the storage event', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.sideSheetWidth()).toBe(280);

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'sideSheetWidth',
        newValue: '420',
        storageArea: localStorage,
      }),
    );
    expect(svc.sideSheetWidth()).toBe(420);
  });

  it('ignores storage events targeting unrelated keys', () => {
    const svc = TestBed.inject(UiPreferencesService);
    const before = {
      nav: svc.taskNavCollapsed(),
      width: svc.sideSheetWidth(),
      metrics: svc.treeMetricView(),
    };
    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'someUnrelatedKey',
        newValue: 'whatever',
        storageArea: localStorage,
      }),
    );
    expect(svc.taskNavCollapsed()).toBe(before.nav);
    expect(svc.sideSheetWidth()).toBe(before.width);
    expect(svc.treeMetricView()).toBe(before.metrics);
  });
});
