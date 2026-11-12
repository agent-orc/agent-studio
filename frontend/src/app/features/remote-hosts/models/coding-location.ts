import type { RemoteHost } from './remote-host.model';

/** Empty means the location can accept coding work. */
export function codingLocationUnavailableReason(host: RemoteHost, localCodingAdmitted: boolean): string {
  if (host.role === 'local') return localCodingAdmitted ? '' : 'Local coding is disabled';
  if (host.role === 'remote' && host.serviceRole !== 'coding') {
    return host.serviceRole === 'review' ? 'Review-only runner' : 'Coding role not confirmed';
  }
  if (host.status === 'retired') return 'Retired';
  if (host.status === 'offline') return 'Offline';
  if (host.status === 'draining') return 'Draining';
  return '';
}
