import { describe, expect, it } from 'vitest';
import {
  aggregateAutoPickup,
  deriveProjectAutoPickupByName,
} from './studio-shell.auto-pickup';

describe('Explorer auto-pickup state model', () => {
  it('derives all four states and preserves a build-profile block reason', () => {
    const rows = ['active', 'paused', 'manual', 'blocked'].map(name => ({ name }));
    const result = deriveProjectAutoPickupByName(
      {
        active: { mode: 'auto-continuous' },
        paused: { mode: 'paused' },
        manual: { mode: 'manual' },
        blocked: { mode: 'auto-continuous' },
      },
      {
        active: { pickupAllowed: true, buildProfileStatus: 'pipeline-ready' },
        blocked: { pickupAllowed: false, buildProfileStatus: 'declared' },
      },
      rows,
    );

    expect(result.get('active')).toEqual({
      state: 'active',
      reason: null,
      tooltip: 'Auto-pickup active',
    });
    expect(result.get('paused')?.state).toBe('paused');
    expect(result.get('manual')?.state).toBe('manual');
    expect(result.get('blocked')).toEqual({
      state: 'blocked',
      reason: 'build profile declared',
      tooltip: 'Auto-pickup blocked: build profile declared',
    });
  });

  it('uses validation-specific reasons and lets blocked win an aggregate', () => {
    const result = deriveProjectAutoPickupByName(
      {
        alpha: { mode: 'auto-continuous' },
        beta: { mode: 'auto-continuous' },
      },
      {
        alpha: { pickupAllowed: true },
        beta: { pickupAllowed: false, buildProfileStatus: 'validation-failed' },
      },
      [{ name: 'alpha' }, { name: 'beta' }],
    );

    expect(result.get('beta')?.tooltip).toBe('Auto-pickup blocked: build profile validation failed');
    expect(aggregateAutoPickup(['alpha', 'beta'], result)).toEqual({
      state: 'blocked',
      autoProjects: ['alpha', 'beta'],
    });
  });

  it('uses project pickup settings when a remote project has no local runner', () => {
    const result = deriveProjectAutoPickupByName(
      {},
      {},
      [{ name: 'remote-auto' }, { name: 'remote-manual' }],
      { 'remote-auto': 'auto', 'remote-manual': 'manual' },
    );

    expect(result.get('remote-auto')?.state).toBe('active');
    expect(result.get('remote-manual')?.state).toBe('manual');
  });
});
