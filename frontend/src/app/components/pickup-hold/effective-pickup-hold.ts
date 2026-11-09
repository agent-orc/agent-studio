import { TaskState, type PickupHoldStatus, type TaskInfo } from '../../models/task.model';
import { claimLoadGateLabel, type RemoteHost } from '../../features/remote-hosts';

/**
 * The hold a card shows: its own server projection, or, for a Ready card
 * routed at a remote host whose claim-load gate is closed, the gate itself.
 * Shared by the card's hold block and the Ready lane header split so both
 * count the same card under the same hold.
 */
export function effectivePickupHold(task: TaskInfo, hosts: readonly RemoteHost[]): PickupHoldStatus | null {
  if (task.pickupHold) return task.pickupHold;
  if (task.state !== TaskState.Ready) return null;
  const runnerId = task.executionLocation?.configuredRunnerId;
  if (!runnerId) return null;
  const host = hosts.find(item =>
    item.clientId === runnerId || item.id === runnerId || item.capacityHostId === runnerId);
  const gate = host?.claimLoadGate;
  if (!gate || gate.state !== 'closed' || !gate.since) return null;
  return {
    classification: 'satisfiable-soon',
    mechanism: 'claim-load-gate',
    reason: `${host.name} is ${claimLoadGateLabel(gate).toLowerCase()}. Running work continues; new claims resume when load falls.`,
    sinceUtc: gate.since,
    unsatisfiable: false,
    resolutions: [],
  };
}
