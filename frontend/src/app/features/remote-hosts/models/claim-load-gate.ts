import type { ClaimLoadGate } from './remote-host.model';

export function claimLoadGateLabel(gate: ClaimLoadGate): string {
  const load = gate.loadPerCore.toFixed(1);
  const threshold = gate.threshold.toFixed(1);
  if (gate.state === 'closed') {
    const minutes = Math.max(1, Math.round(gate.sustainedSeconds / 60));
    return `Not claiming: load ${load} per core above ${threshold} for ${minutes} min`;
  }
  return gate.loadPerCore <= gate.threshold
    ? `Claiming: load ${load} per core below ${threshold}`
    : `Claiming: load ${load} per core above ${threshold}, waiting for sustained threshold`;
}
