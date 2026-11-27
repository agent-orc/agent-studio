import { describe, expect, it, beforeEach, vi, afterEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { NotificationService } from './notification.service';

describe('NotificationService', () => {
  let service: NotificationService;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
    service = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('appends each notify call to the stack', () => {
    service.success('saved');
    service.error('oops');
    expect(service.notifications().map((n) => n.kind)).toEqual(['success', 'error']);
  });

  it('auto-dismisses after the per-kind default duration', () => {
    service.success('saved');
    expect(service.notifications()).toHaveLength(1);
    vi.advanceTimersByTime(4999);
    expect(service.notifications()).toHaveLength(1);
    vi.advanceTimersByTime(2);
    expect(service.notifications()).toHaveLength(0);
  });

  it('warning and error are persistent by default (durationMs=0)', () => {
    service.warning('watch out');
    service.error('oops');
    vi.advanceTimersByTime(60_000);
    expect(service.notifications()).toHaveLength(2);
  });

  it('actions force persistent (durationMs=0) regardless of kind', () => {
    service.notify({
      message: 'reload required',
      kind: 'success',
      actions: [{ label: 'Reload', callback: () => undefined }],
    });
    vi.advanceTimersByTime(60_000);
    expect(service.notifications()).toHaveLength(1);
  });

  it('dismissTopmost removes only the first toast', () => {
    service.success('first');
    service.info('second');
    expect(service.notifications()).toHaveLength(2);
    service.dismissTopmost();
    expect(service.notifications()).toHaveLength(1);
    expect(service.notifications()[0].message).toBe('second');
  });

  it('respects an explicit durationMs override', () => {
    service.notify({ message: 'sticky', kind: 'info', durationMs: 10_000 });
    vi.advanceTimersByTime(5_000);
    expect(service.notifications()).toHaveLength(1);
    vi.advanceTimersByTime(5_001);
    expect(service.notifications()).toHaveLength(0);
  });

  it('does NOT auto-dismiss when durationMs is 0', () => {
    const id = service.notify({ message: 'sticky', kind: 'info', durationMs: 0 });
    vi.advanceTimersByTime(60_000);
    expect(service.notifications()).toHaveLength(1);
    service.dismiss(id);
    expect(service.notifications()).toHaveLength(0);
  });

  it('dismiss removes the notification and cancels its timer', () => {
    const id = service.success('saved');
    service.dismiss(id);
    expect(service.notifications()).toHaveLength(0);
    // Advancing timers should be a no-op now.
    vi.advanceTimersByTime(10_000);
    expect(service.notifications()).toHaveLength(0);
  });

  it('updates a persistent notification in place', () => {
    const id = service.notify({ message: 'Archiving 0 of 55 tasks...', kind: 'info', durationMs: 0 });

    expect(service.update(id, { message: 'Archiving 21 of 55 tasks...' })).toBe(true);
    expect(service.notifications()).toHaveLength(1);
    expect(service.notifications()[0].id).toBe(id);
    expect(service.notifications()[0].message).toBe('Archiving 21 of 55 tasks...');
    expect(service.update(999, { message: 'missing' })).toBe(false);
  });

  it('dismissAll empties the stack', () => {
    service.info('a');
    service.warning('b');
    service.error('c');
    service.dismissAll();
    expect(service.notifications()).toHaveLength(0);
  });

  it('dismisses the newest sticky notice for an Escape owner and leaves timed ones (AGT-3830)', () => {
    service.warning('older refusal');
    service.success('saved');
    service.warning('newer refusal');
    expect(service.dismissNewestPersistent()).toBe(true);
    expect(service.notifications().map((n) => n.message)).toEqual(['older refusal', 'saved']);
    expect(service.dismissNewestPersistent()).toBe(true);
    expect(service.dismissNewestPersistent()).toBe(false);
    expect(service.notifications().map((n) => n.message)).toEqual(['saved']);
  });
});
