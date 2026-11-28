import { observedStructure } from '../structure.fixtures';
import type { StructureData, StructureLink, StructureNode } from '../structure.model';
import type { LiveSources } from './live-structure.sources';

// AGT-4024: the installation as GET /api/v1/operations/structure and the three optional
// reads return it, for specs and screenshots. Role names only: no real host name, address
// or account appears here. It extends the AGT-3954 fixture of hosts that only report.
const seen = '2026-11-17T12:00:00Z';
const GIB = 1024 ** 3;

const tools = (claudeBehind: boolean): NonNullable<NonNullable<StructureNode['facts']>['warnings']>['tools'] => [
  { name: 'claude', kind: 'cli', version: claudeBehind ? '1.2.0' : '1.3.0', targetVersion: '1.3.0', state: claudeBehind ? 'behind' : 'ok' },
  { name: 'dotnet', kind: 'toolchain', version: '10.0.100', state: 'ok' },
];

function adjust(node: StructureNode): StructureNode {
  const review = /^machine:review-(\d)$/.exec(node.id)?.[1];
  if (review) {
    const n = Number(review);
    return { ...node, facts: { ...node.facts!,
      performance: { ...node.facts!.performance!, cpuPercent: [37, 61, 4][n - 1], memoryUsedBytes: [8, 14, 3][n - 1] * GIB },
      capacity: { busySlots: [1, 2, 0][n - 1], freeSlots: [1, 0, 2][n - 1], totalSlots: 2, runningCards: [['AGT-7'], ['AGT-8', 'AGT-9'], []][n - 1] },
      warnings: { tools: tools(n === 2), missingRequired: n === 3 ? ['toolchain:playwright'] : [], behindRequired: n === 2 ? ['claude'] : [],
        releaseBehind: false, pendingHostInstall: false, linksDown: [],
        blockedCards: n === 3 ? [{ key: 'AGT-10', state: 'refused', reason: 'toolchain:playwright' }] : [] } } };
  }
  if (node.id === 'machine:studio-server') return { ...node, facts: { ...node.facts!, dataLocations: node.facts!.dataLocations!.map(location => ({
    ...location, backupCovered: false,
    coverage: location.role === 'workspace-repository' || location.role === 'project-checkouts' ? 'git-remote' as const : 'none' as const })) } };
  if (node.id === 'machine:gate-1') return { ...node, facts: { ...node.facts!, performance: { observedAt: seen, cpuPercent: 9, cpuCores: 12,
    load1: 0.4, load5: 0.5, load15: 0.6, memoryUsedBytes: 6 * GIB, memoryTotalBytes: 50 * GIB,
    disks: [{ mount: '/srv/gate', purpose: 'gate evidence', usedBytes: 79 * GIB, freeBytes: 121 * GIB, totalBytes: 200 * GIB }] } } };
  return node;
}

const control = (from: string, latencyMs: number): StructureLink => ({
  id: `link:${from}>component:studio-server:api:control`, from, to: 'component:studio-server:api', kind: 'control',
  label: 'Task Server connection', sourceRef: 'host report',
  observation: { difference: 'undeclared', health: 'up', observedAt: seen }, facts: { state: 'up', latencyMs, probedAt: seen, transport: 'https' } });

export const liveStructureFixture: StructureData = {
  ...observedStructure,
  revision: 41,
  nodes: observedStructure.nodes.map(adjust),
  links: [
    ...observedStructure.links,
    control('machine:review-2', 4.8),
    control('machine:review-3', 5.1),
    { id: 'link:machine:studio-server>machine:gate-1:ssh-reverse', from: 'machine:studio-server', to: 'machine:gate-1', kind: 'ssh-reverse',
      label: 'Runner link', sourceRef: 'host report', observation: { difference: 'undeclared', health: 'up', observedAt: seen },
      facts: { state: 'up', latencyMs: null, probedAt: seen, transport: 'ssh-reverse' } },
  ],
};

const runner = (hostId: string, role: string, restarts: number): NonNullable<LiveSources['runners']>[number] => ({
  runnerId: `${hostId}-${role}`, name: `${hostId} ${role}`, hostId, runnerVersion: '2.0', status: 'active',
  registeredAt: '2026-11-12T08:00:00Z', lastSeenAt: seen, restartedAt: restarts ? '2026-11-17T09:30:00Z' : '2026-11-15T06:00:00Z',
  reviewRestarts: role === 'review' ? Array.from({ length: restarts }, () => ({ restartedAt: '2026-11-17T09:30:00Z', reviewsLost: 1 })) : null,
  release: { version: '2.0', commit: '0a1b2c3', builtAt: '2026-11-11T16:00:00Z', imageRef: `registry.example/studio/${role}-host:2.0` },
});

/** Backups as the Studio reports them before a target exists: one local archive, nothing off the host. */
export const liveSourcesFixture: LiveSources = {
  backup: {
    state: 'not-configured', configured: false, running: false, intervalHours: 24, retentionCount: 7, nextBackupAt: null,
    lastGood: { id: 'backup-20261109', createdAt: '2026-11-09T03:00:00Z', sizeBytes: 3 * GIB, fileCount: 1200, verification: 'unverified', offHost: 'not-configured' },
    lastGoodAgeHours: 201, stale: true, lastRun: { at: '2026-11-09T03:00:00Z', outcome: 'succeeded', error: null }, lastVerification: null,
    offHost: { kind: null, state: 'not-configured', lastUploadedAt: null, lastError: null },
    warnings: [{ code: 'not-configured', severity: 'warning', message: 'Backups are not configured: no encryption key and no target outside this host.' }],
    generatedAt: seen,
  },
  runners: [runner('runner-host', 'coding', 0), runner('review-1', 'review', 0), runner('review-2', 'review', 1), runner('review-3', 'review', 0), runner('gate-1', 'gate', 0)],
  environment: {
    gateWorkers: [{ id: 'gate-1', name: 'gate-1', slots: 2, busy: 0, lastHeartbeat: seen, currentRun: null }],
    taskServer: { version: '2.0', commit: '0a1b2c3' }, engine: { state: 'ready', lastContact: seen },
  },
};

/**
 * AGT-4203: serialized from ObservedStructureProjection.Apply at develop 51522ee9fc.
 * Inputs: coding-runner on coding-host (2/4), shared-coding (2/4) and shared-review
 * (1/2) on shared-host, at 2026-11-27T12:00:00Z. Optional null properties omitted.
 * Both the host aggregates and the process facts are retained, as the API emits them.
 */
export const projectedCapacityFixture: StructureData = {
  "nodes": [
    {
      "id": "installation",
      "kind": "boundary",
      "label": "Installation",
      "parentId": null,
      "unitId": null,
      "environmentId": null,
      "project": null,
      "provenance": "declared",
      "sourceRef": "capacity regression fixture",
      "otherProject": false,
      "observation": {
        "difference": "declared-only",
        "health": "not observed",
        "behind": false
      }
    },
    {
      "id": "machine:coding-host",
      "kind": "host",
      "label": "coding-host",
      "parentId": "installation",
      "unitId": null,
      "environmentId": null,
      "project": null,
      "provenance": "observed",
      "sourceRef": "host report",
      "otherProject": false,
      "observation": {
        "difference": "undeclared",
        "health": "up",
        "observedAt": "2026-11-27T12:00:00Z",
        "behind": false,
        "activeGates": 0,
        "detail": "The Stable release identity is unavailable on this server."
      },
      "facts": {
        "hostId": "coding-host",
        "roles": [
          "coding"
        ],
        "lastSeenAt": "2026-11-27T12:00:00Z",
        "capacity": {
          "busySlots": 2,
          "freeSlots": 2,
          "totalSlots": 4,
          "runningCards": [
            "AGT-1",
            "AGT-2"
          ]
        },
        "release": {
          "version": "2.0",
          "state": "unknown",
          "behind": false,
          "pendingInstall": false,
          "detail": "The Stable release identity is unavailable on this server."
        },
        "warnings": {
          "tools": [],
          "missingRequired": [],
          "behindRequired": [],
          "releaseBehind": false,
          "pendingHostInstall": false,
          "linksDown": [],
          "blockedCards": []
        }
      }
    },
    {
      "id": "component:coding-host:coding-runner",
      "kind": "process",
      "label": "Coding daemon",
      "parentId": "machine:coding-host",
      "unitId": "coding-runner",
      "environmentId": null,
      "project": null,
      "provenance": "observed",
      "sourceRef": "host report",
      "role": "coding",
      "otherProject": false,
      "observation": {
        "difference": "undeclared",
        "health": "up",
        "observedAt": "2026-11-27T12:00:00Z",
        "behind": false,
        "activeGates": 0,
        "detail": "The Stable release identity is unavailable on this server."
      },
      "facts": {
        "hostId": "coding-host",
        "roles": [
          "coding"
        ],
        "lastSeenAt": "2026-11-27T12:00:00Z",
        "capacity": {
          "busySlots": 2,
          "freeSlots": 2,
          "totalSlots": 4,
          "runningCards": [
            "AGT-1",
            "AGT-2"
          ]
        },
        "release": {
          "version": "2.0",
          "state": "unknown",
          "behind": false,
          "pendingInstall": false,
          "detail": "The Stable release identity is unavailable on this server."
        },
        "warnings": {
          "tools": [],
          "missingRequired": [],
          "behindRequired": [],
          "releaseBehind": false,
          "pendingHostInstall": false,
          "linksDown": [],
          "blockedCards": []
        }
      }
    },
    {
      "id": "machine:shared-host",
      "kind": "host",
      "label": "shared-host",
      "parentId": "installation",
      "unitId": null,
      "environmentId": null,
      "project": null,
      "provenance": "observed",
      "sourceRef": "host report",
      "otherProject": false,
      "observation": {
        "difference": "undeclared",
        "health": "up",
        "observedAt": "2026-11-27T12:00:00Z",
        "behind": false,
        "activeGates": 0,
        "detail": "The Stable release identity is unavailable on this server."
      },
      "facts": {
        "hostId": "shared-host",
        "roles": [
          "coding",
          "review"
        ],
        "lastSeenAt": "2026-11-27T12:00:00Z",
        "capacity": {
          "busySlots": 3,
          "freeSlots": 3,
          "totalSlots": 6,
          "runningCards": [
            "AGT-3",
            "AGT-4",
            "AGT-5"
          ]
        },
        "release": {
          "version": "2.0",
          "state": "unknown",
          "behind": false,
          "pendingInstall": false,
          "detail": "The Stable release identity is unavailable on this server."
        },
        "warnings": {
          "tools": [],
          "missingRequired": [],
          "behindRequired": [],
          "releaseBehind": false,
          "pendingHostInstall": false,
          "linksDown": [],
          "blockedCards": []
        }
      }
    },
    {
      "id": "component:shared-host:shared-coding",
      "kind": "process",
      "label": "Coding daemon",
      "parentId": "machine:shared-host",
      "unitId": "shared-coding",
      "environmentId": null,
      "project": null,
      "provenance": "observed",
      "sourceRef": "host report",
      "role": "coding",
      "otherProject": false,
      "observation": {
        "difference": "undeclared",
        "health": "up",
        "observedAt": "2026-11-27T12:00:00Z",
        "behind": false,
        "activeGates": 0,
        "detail": "The Stable release identity is unavailable on this server."
      },
      "facts": {
        "hostId": "shared-host",
        "roles": [
          "coding"
        ],
        "lastSeenAt": "2026-11-27T12:00:00Z",
        "capacity": {
          "busySlots": 2,
          "freeSlots": 2,
          "totalSlots": 4,
          "runningCards": [
            "AGT-3",
            "AGT-4"
          ]
        },
        "release": {
          "version": "2.0",
          "state": "unknown",
          "behind": false,
          "pendingInstall": false,
          "detail": "The Stable release identity is unavailable on this server."
        },
        "warnings": {
          "tools": [],
          "missingRequired": [],
          "behindRequired": [],
          "releaseBehind": false,
          "pendingHostInstall": false,
          "linksDown": [],
          "blockedCards": []
        }
      }
    },
    {
      "id": "component:shared-host:shared-review",
      "kind": "process",
      "label": "Review daemon",
      "parentId": "machine:shared-host",
      "unitId": "shared-review",
      "environmentId": null,
      "project": null,
      "provenance": "observed",
      "sourceRef": "host report",
      "role": "review",
      "otherProject": false,
      "observation": {
        "difference": "undeclared",
        "health": "up",
        "observedAt": "2026-11-27T12:00:00Z",
        "behind": false,
        "activeGates": 0,
        "detail": "The Stable release identity is unavailable on this server."
      },
      "facts": {
        "hostId": "shared-host",
        "roles": [
          "review"
        ],
        "lastSeenAt": "2026-11-27T12:00:00Z",
        "capacity": {
          "busySlots": 1,
          "freeSlots": 1,
          "totalSlots": 2,
          "runningCards": [
            "AGT-5"
          ]
        },
        "release": {
          "version": "2.0",
          "state": "unknown",
          "behind": false,
          "pendingInstall": false,
          "detail": "The Stable release identity is unavailable on this server."
        },
        "warnings": {
          "tools": [],
          "missingRequired": [],
          "behindRequired": [],
          "releaseBehind": false,
          "pendingHostInstall": false,
          "linksDown": [],
          "blockedCards": []
        }
      }
    }
  ],
  "links": [],
  "revision": 0,
  "generatedAt": "2026-11-27T12:00:00Z",
  "schemaVersion": 2
};

/** Two independently released daemons share one host, as identified by the projection's unitId. */
export const sharedRunnerStructureFixture: StructureData = {
  ...projectedCapacityFixture,
  nodes: projectedCapacityFixture.nodes.map(node => {
    const version = node.unitId === 'shared-review' ? '2.2' : '2.1';
    return node.facts?.hostId === 'shared-host' ? { ...node, facts: { ...node.facts,
      release: { ...node.facts.release!, version } } } : node;
  }),
};
export const sharedRunnerSourcesFixture: LiveSources = {
  backup: null, environment: null,
  runners: [
    { ...runner('shared-host', 'coding', 0), runnerId: 'shared-coding',
      restartedAt: '2026-11-27T08:00:00Z', reviewRestarts: [],
      release: { version: '2.1', imageRef: 'registry.example/studio/coding-host:2.1' } },
    { ...runner('shared-host', 'review', 2), runnerId: 'shared-review',
      restartedAt: '2026-11-27T11:00:00Z',
      reviewRestarts: [{ restartedAt: '2026-11-27T11:00:00Z', reviewsLost: 3 },
        { restartedAt: '2026-11-26T09:00:00Z', reviewsLost: 1 }],
      release: { version: '2.2', imageRef: 'registry.example/studio/review-host:2.2' } },
  ],
};
