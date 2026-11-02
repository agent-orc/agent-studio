import type { RemoteHost } from './remote-host.model';

export function roleSlotTotal(host: RemoteHost): number | null {
  if (host.serviceRole === 'review'
    && host.reviewPlane?.currentCeiling !== null
    && host.reviewPlane?.currentCeiling !== undefined) {
    return host.reviewPlane.currentCeiling;
  }
  if (host.serviceRole === 'review' && host.roleMaxParallelism) return host.roleMaxParallelism;
  if (host.runtimeCapacity) return host.runtimeCapacity.maxParallelism;
  if (host.effectiveMaxParallelism) return host.effectiveMaxParallelism;
  if (host.roleMaxParallelism) return host.roleMaxParallelism;
  if (host.activeTaskCount !== undefined && host.availableSlots !== undefined) {
    const reported = host.activeTaskCount + host.availableSlots;
    return reported > 0 ? reported : null;
  }
  return null;
}
