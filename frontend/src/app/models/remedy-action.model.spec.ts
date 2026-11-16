import { describe, expect, it } from 'vitest';
import { executionHostRoute, remedyLinkLabel } from './remedy-action.model';

describe('remedy action labels', () => {
  // AGT-3840: the pickup hold way out named a list; it now names the host and key.
  it('names the host and capability an Execution Hosts action opens', () => {
    expect(remedyLinkLabel({ kind: 'route', target: 'execution-hosts' })).toBe('Open Execution Hosts');
    expect(remedyLinkLabel({ kind: 'route', target: 'execution-hosts', hostId: 'kori-review-01-runner' }))
      .toBe('Open kori-review-01-runner');
    expect(remedyLinkLabel({
      kind: 'route', target: 'execution-hosts', hostId: 'kori-review-01-runner', capability: 'provider-auth:codex',
    })).toBe('Open provider-auth:codex on kori-review-01-runner');
  });

  it('builds the host row route with the focused capability', () => {
    expect(executionHostRoute('kori-review-01-runner'))
      .toBe('/workspace/settings/environment?host=kori-review-01-runner');
    expect(executionHostRoute('kori-review-01-runner', 'provider-auth:codex'))
      .toBe('/workspace/settings/environment?host=kori-review-01-runner&capability=provider-auth%3Acodex');
  });
});
