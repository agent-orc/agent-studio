import { describe, expect, it } from 'vitest';
import type { RemoteHost } from './remote-host.model';
import { roleSlotTotal } from './role-slots';

const ROLE = { serviceRole: 'review', roleMaxParallelism: 6 } as RemoteHost;

describe('roleSlotTotal', () => {
  it('uses the review ceiling before the advertised role capacity', () => {
    expect(roleSlotTotal(ROLE)).toBe(6);
    expect(roleSlotTotal({ ...ROLE, reviewPlane: { currentCeiling: 2 } } as RemoteHost)).toBe(2);
  });

  it('falls back to runtime capacity and reported slots', () => {
    expect(roleSlotTotal({ serviceRole: 'coding', runtimeCapacity: { maxParallelism: 8 } } as RemoteHost)).toBe(8);
    expect(roleSlotTotal({ activeTaskCount: 2, availableSlots: 3 } as RemoteHost)).toBe(5);
    expect(roleSlotTotal({ activeTaskCount: 0, availableSlots: 0 } as RemoteHost)).toBeNull();
  });
});
