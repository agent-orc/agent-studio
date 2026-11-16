/**
 * Pure model for the Explorer project's always-visible auto-pickup mini state.
 * It describes configuration and admission, not whether a task is executing.
 */
export type ProjectAutoPickupState = 'active' | 'paused' | 'manual' | 'blocked';

export interface ProjectPickupGate {
  pickupAllowed: boolean;
  buildProfileStatus?: string | null;
}

export interface ProjectAutoPickupIndicator {
  state: ProjectAutoPickupState;
  tooltip: string;
  reason: string | null;
}

export interface ExplorerAutoPickupAggregate {
  state: 'off' | 'active' | 'blocked';
  autoProjects: readonly string[];
}

export type ProjectPickupMode = 'auto' | 'manual' | 'paused';

/** Project settings own pickup intent; local runner status supplies a fallback while settings load. */
export function projectRunnerMode(
  name: string,
  pickupModes: Readonly<Record<string, ProjectPickupMode>>,
  projects: Readonly<Record<string, { mode?: string } | undefined>>,
): string {
  const pickupMode = pickupModes[name];
  if (pickupMode === 'auto') return 'auto-continuous';
  if (pickupMode === 'manual' || pickupMode === 'paused') return pickupMode;
  return projects[name]?.mode ?? 'manual';
}

function blockReason(gate: ProjectPickupGate): string {
  switch (gate.buildProfileStatus) {
    case 'validating':
      return 'build profile validation in progress';
    case 'validation-failed':
      return 'build profile validation failed';
    case 'declared':
      return 'build profile declared';
    default:
      return 'pickup gate closed';
  }
}

function indicator(state: ProjectAutoPickupState, reason: string | null = null): ProjectAutoPickupIndicator {
  switch (state) {
    case 'active':
      return { state, reason, tooltip: 'Auto-pickup active' };
    case 'paused':
      return { state, reason, tooltip: 'Auto-pickup paused' };
    case 'blocked':
      return { state, reason, tooltip: `Auto-pickup blocked: ${reason ?? 'pickup gate closed'}` };
    default:
      return { state, reason, tooltip: 'Auto-pickup manual' };
  }
}

/**
 * Derive one indicator for every rendered project. `auto-continuous` is active
 * only while the admission gate is open; a closed build-profile gate wins and
 * exposes its reason. Paused and manual remain visible instead of collapsing
 * to an empty slot.
 */
export function deriveProjectAutoPickupByName(
  projects: Readonly<Record<string, { mode?: string } | undefined>>,
  gates: Readonly<Record<string, ProjectPickupGate | undefined>>,
  rows: readonly { name: string }[],
  pickupModes: Readonly<Record<string, ProjectPickupMode>> = {},
): ReadonlyMap<string, ProjectAutoPickupIndicator> {
  const map = new Map<string, ProjectAutoPickupIndicator>();
  for (const row of rows) {
    const mode = projectRunnerMode(row.name, pickupModes, projects);
    const gate = gates[row.name];
    if (mode === 'auto-continuous') {
      map.set(row.name, gate?.pickupAllowed === false
        ? indicator('blocked', blockReason(gate))
        : indicator('active'));
    } else if (mode === 'paused') {
      map.set(row.name, indicator('paused'));
    } else {
      map.set(row.name, indicator('manual'));
    }
  }
  return map;
}

/**
 * Collapsed workspace headers keep the existing auto summary, but only active
 * or blocked auto-continuous projects participate. A blocked gate wins because
 * it requires operator action.
 */
export function aggregateAutoPickup(
  names: readonly string[],
  indicators: ReadonlyMap<string, ProjectAutoPickupIndicator>,
): ExplorerAutoPickupAggregate {
  const autoProjects = names
    .filter(name => {
      const state = indicators.get(name)?.state;
      return state === 'active' || state === 'blocked';
    })
    .sort((a, b) => a.localeCompare(b));
  const blocked = autoProjects.some(name => indicators.get(name)?.state === 'blocked');
  return {
    state: blocked ? 'blocked' : autoProjects.length > 0 ? 'active' : 'off',
    autoProjects,
  };
}

export function aggregateAutoPickupTooltip(aggregate: ExplorerAutoPickupAggregate): string {
  if (aggregate.state === 'off') return '';
  const lead = aggregate.state === 'blocked' ? 'Auto-pickup blocked' : 'Auto-pickup active';
  return `${lead}: ${aggregate.autoProjects.join(', ')}`;
}
