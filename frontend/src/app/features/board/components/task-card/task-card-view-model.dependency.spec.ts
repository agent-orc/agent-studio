import { describe, expect, it } from 'vitest';
import { buildDependencyChip } from './task-card-view-model';
import type { WaitsOnItem, WaitsOnStatus } from '../../../../models/task.model';

/**
 * AGT-2029: the waits-on dependency chip renders the backend-computed status
 * (fulfilled/open per target, blocked, cycle) so the card matches the runner's
 * own pickup gate. These pin the label, tone, glyph, and navigation target the
 * card derives from that status.
 */
function item(overrides: Partial<WaitsOnItem> = {}): WaitsOnItem {
  return {
    key: 'DEP-1',
    resolved: true,
    fulfilled: false,
    releaseGate: false,
    targetReleased: false,
    waitingForRelease: false,
    targetJobId: 'dep-1',
    targetTitle: 'A dependency',
    targetState: '2-ready',
    targetWatchPath: '/ws/lib',
    ...overrides,
  };
}

function status(overrides: Partial<WaitsOnStatus> = {}): WaitsOnStatus {
  return { items: [item()], blocked: true, cycleDetected: false, ...overrides };
}

describe('buildDependencyChip', () => {
  it('returns null when there are no dependencies', () => {
    expect(buildDependencyChip(null)).toBeNull();
    expect(buildDependencyChip(undefined)).toBeNull();
    expect(buildDependencyChip(status({ items: [] }))).toBeNull();
  });

  it('renders an open (waiting) chip for an unfulfilled dependency', () => {
    const chip = buildDependencyChip(status());
    expect(chip).not.toBeNull();
    expect(chip!.tone).toBe('open');
    expect(chip!.glyph).toBe('⏳');
    expect(chip!.label).toBe('waits for completion: DEP-1');
    // navigation target comes from the backend-resolved fields
    expect(chip!.targetJobId).toBe('dep-1');
    expect(chip!.targetWatchPath).toBe('/ws/lib');
  });

  it('uses the full count when a slim card carries three dependency labels', () => {
    const chip = buildDependencyChip(status({
      itemCount: 5,
      openCount: 5,
      items: [item(), item({ key: 'DEP-2' }), item({ key: 'DEP-3' })],
    }));
    expect(chip?.label).toBe('waits for completion: DEP-1 +4');
    expect(chip?.tooltip).toContain('+2 more dependencies');
  });

  it('summarises multiple open dependencies with a +N suffix and points at the first open one', () => {
    const chip = buildDependencyChip(
      status({
        items: [
          item({ key: 'DEP-1', fulfilled: true, targetState: '6-completed' }),
          item({ key: 'DEP-2', fulfilled: false, targetJobId: 'dep-2' }),
          item({ key: 'DEP-3', fulfilled: false, targetJobId: 'dep-3' }),
        ],
      }),
    );
    expect(chip!.tone).toBe('open');
    expect(chip!.label).toBe('waits for completion: DEP-2 +1');
    expect(chip!.targetJobId).toBe('dep-2');
  });

  it('renders a ready chip when every dependency is fulfilled', () => {
    const chip = buildDependencyChip(
      status({
        blocked: false,
        items: [item({ fulfilled: true, targetState: '6-completed' })],
      }),
    );
    expect(chip!.tone).toBe('ready');
    expect(chip!.glyph).toBe('✓');
    expect(chip!.label).toBe('DEP-1');
  });

  it('distinguishes a terminal dependency that still waits for explicit release', () => {
    const chip = buildDependencyChip(
      status({
        items: [item({
          targetState: '6-completed',
          releaseGate: true,
          waitingForRelease: true,
        })],
      }),
    );

    expect(chip!.tone).toBe('open');
    expect(chip!.label).toBe('waits for release: DEP-1');
    expect(chip!.tooltip).toContain('completed, release pending');
  });

  it('renders a cycle chip (config error) regardless of item states', () => {
    const chip = buildDependencyChip(status({ cycleDetected: true }));
    expect(chip!.tone).toBe('cycle');
    expect(chip!.glyph).toBe('⚠');
    expect(chip!.label).toBe('dep cycle');
    expect(chip!.tooltip).toContain('cycle');
  });

  it('marks an unknown (not-yet-created) target as open with no nav target', () => {
    const chip = buildDependencyChip(
      status({
        items: [
          item({
            key: 'GHOST-9',
            resolved: false,
            fulfilled: false,
            targetJobId: null,
            targetTitle: null,
            targetState: null,
            targetWatchPath: null,
          }),
        ],
      }),
    );
    expect(chip!.tone).toBe('open');
    expect(chip!.label).toBe('waits for completion: GHOST-9');
    expect(chip!.targetJobId).toBeNull();
    expect(chip!.tooltip).toContain('not created yet');
  });
});
