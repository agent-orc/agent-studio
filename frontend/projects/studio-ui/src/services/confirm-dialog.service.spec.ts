import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ConfirmDialogService } from './confirm-dialog.service';

describe('ConfirmDialogService', () => {
  let service: ConfirmDialogService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
    service = TestBed.inject(ConfirmDialogService);
  });

  it('resolves true when accept is called', async () => {
    const p = service.confirm({ title: 't', message: 'm' });
    expect(service.active()?.title).toBe('t');
    service.accept();
    expect(await p).toBe(true);
    expect(service.active()).toBeNull();
  });

  it('resolves false when cancel is called', async () => {
    const p = service.confirm({ title: 't', message: 'm' });
    service.cancel();
    expect(await p).toBe(false);
    expect(service.active()).toBeNull();
  });

  it('defaults kind to danger and labels to Confirm / Cancel', () => {
    void service.confirm({ title: 't', message: 'm' });
    const state = service.active();
    expect(state?.kind).toBe('danger');
    expect(state?.confirmLabel).toBe('Confirm');
    expect(state?.cancelLabel).toBe('Cancel');
    service.cancel();
  });

  it('preserves caller-supplied labels and detail', () => {
    void service.confirm({
      title: 'Delete?',
      message: 'Are you sure?',
      detail: 'job-123',
      confirmLabel: 'Delete',
      cancelLabel: 'Keep',
      kind: 'danger',
    });
    const state = service.active();
    expect(state?.detail).toBe('job-123');
    expect(state?.confirmLabel).toBe('Delete');
    expect(state?.cancelLabel).toBe('Keep');
    service.cancel();
  });

  it('auto-rejects a previous pending confirm when a new one opens', async () => {
    const first = service.confirm({ title: 'first', message: 'm' });
    const second = service.confirm({ title: 'second', message: 'm' });
    expect(service.active()?.title).toBe('second');
    expect(await first).toBe(false);
    service.accept();
    expect(await second).toBe(true);
  });
});
