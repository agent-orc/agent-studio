import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { StudioPanelStateService } from './studio-panel-state.service';

const STORAGE_KEY = 'atp.studio.panelState.v1';

describe('StudioPanelStateService', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(STORAGE_KEY);
  });

  it('persists the active sidebar panel and hidden state across service reloads', () => {
    let service = TestBed.inject(StudioPanelStateService);

    expect(service.active()).toBe('explorer');
    expect(service.visible()).toBe(true);

    service.toggle('explorer');
    expect(service.visible()).toBe(false);
    expect(JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}')).toEqual({
      active: 'explorer',
      visible: false,
    });

    TestBed.resetTestingModule();
    service = TestBed.inject(StudioPanelStateService);
    expect(service.active()).toBe('explorer');
    expect(service.visible()).toBe(false);

    service.toggle('filters');
    expect(service.active()).toBe('filters');
    expect(service.visible()).toBe(true);
  });

  it('ignores stale panel names and keeps a safe default', () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ active: 'ghost', visible: false }));
    const service = TestBed.inject(StudioPanelStateService);

    expect(service.active()).toBe('explorer');
    expect(service.visible()).toBe(false);
  });

  it('opens a panel explicitly without toggling it closed', () => {
    const service = TestBed.inject(StudioPanelStateService);

    service.open('settings');
    service.open('settings');

    expect(service.active()).toBe('settings');
    expect(service.visible()).toBe(true);
    expect(JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}')).toEqual({
      active: 'settings',
      visible: true,
      markerOwner: 'panel',
    });
  });

  it('records panel and editor activation as the marker owner and restores it after reload', () => {
    let service = TestBed.inject(StudioPanelStateService);
    expect(service.markerOwner()).toBeUndefined();

    service.open('chats');
    expect(service.markerOwner()).toBe('panel');

    TestBed.resetTestingModule();
    service = TestBed.inject(StudioPanelStateService);
    expect(service.markerOwner()).toBe('panel');

    service.noteEditorActivation();
    expect(service.markerOwner()).toBe('editor');
    expect(service.active()).toBe('chats');
    expect(service.visible()).toBe(true);

    TestBed.resetTestingModule();
    service = TestBed.inject(StudioPanelStateService);
    expect(service.markerOwner()).toBe('editor');
  });

  it('treats a missing or invalid stored marker owner as unset', () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ active: 'filters', visible: true, markerOwner: 42 }));
    const service = TestBed.inject(StudioPanelStateService);

    expect(service.active()).toBe('filters');
    expect(service.markerOwner()).toBeUndefined();
  });
});
