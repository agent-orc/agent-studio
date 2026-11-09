import { describe, expect, it } from 'vitest';
import type { PickupHoldResolution } from '../../models/task.model';
import type { RemedyAction } from '../../models/remedy-action.model';
import { controlFor } from './pickup-hold-resolution.component';

const navigation = { canOpen: (action: RemedyAction | null | undefined) => action?.target !== 'unknown-route' };
const way = (kind: string, action: RemedyAction | null, patch: Partial<PickupHoldResolution> = {}): PickupHoldResolution =>
  ({ kind, label: kind, detail: '', action, ...patch });

// Rendering is covered through PickupHoldComponent; this pins the control rule.
describe('controlFor', () => {
  it('maps each typed action to its control', () => {
    const api = (target: string): RemedyAction => ({ kind: 'api', target, project: 'demo' });
    expect(controlFor(api('set-execution-runner'), way('route-to-remote-runner', null), navigation)).toBe('runner-picker');
    expect(controlFor(api('set-cli-type'), way('assign-agent', null), navigation)).toBe('cli-picker');
    expect(controlFor(api('start-run'), way('wait-out-backoff', null), navigation)).toBe('api');
    expect(controlFor({ kind: 'setting', target: 'execution-runner', project: 'demo' }, way('x', null), navigation)).toBe('link');
  });

  it('keeps a malformed, unknown, or missing action as explanatory text', () => {
    const edit: RemedyAction = { kind: 'api', target: 'edit-waits-on' };
    expect(controlFor(null, way('drop-dependency', null), navigation)).toBe('none');
    expect(controlFor(edit, way('drop-dependency', edit), navigation)).toBe('none');
    expect(controlFor(edit, way('drop-cycle-edge', edit, { targetKey: 'APP-2' }), navigation)).toBe('none');
    expect(controlFor({ kind: 'api', target: 'format-disk' }, way('x', null), navigation)).toBe('none');
    expect(controlFor({ kind: 'route', target: 'unknown-route' }, way('x', null), navigation)).toBe('none');
    expect(controlFor({ kind: 'shell', target: 'PUT /api/x' }, way('x', null), navigation)).toBe('none');
  });
});
