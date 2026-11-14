import { describe, expect, it } from 'vitest';
import type { RemoteHost } from './remote-host.model';
import { codingLocationUnavailableReason } from './coding-location';

const host = (role: RemoteHost['serviceRole'], status: RemoteHost['status'] = 'online') =>
  ({ id: 'runner', role: 'remote', serviceRole: role, status } as RemoteHost);

describe('codingLocationUnavailableReason', () => {
  it('requires an explicit coding role for remote runners', () => {
    expect(codingLocationUnavailableReason(host('coding'), false)).toBe('');
    expect(codingLocationUnavailableReason(host('review'), false)).toBe('Review-only runner');
    expect(codingLocationUnavailableReason(host(undefined), false)).toBe('Coding role not confirmed');
  });

  it('reports the reason a location cannot accept coding', () => {
    expect(codingLocationUnavailableReason(host('coding', 'draining'), true)).toBe('Draining');
    expect(codingLocationUnavailableReason(host('coding', 'offline'), true)).toBe('Offline');
    expect(codingLocationUnavailableReason({ role: 'local' } as RemoteHost, false))
      .toBe('Local coding is disabled');
    expect(codingLocationUnavailableReason({ role: 'local' } as RemoteHost, true)).toBe('');
  });
});
