import type { EnvironmentHealthCheck, EnvironmentHealthResponse } from '../app/features/environment-health';

/** Shared IP-14 health fixture for the panel and overview specs. */
const check = (overrides: Partial<EnvironmentHealthCheck>): EnvironmentHealthCheck => ({
  id: 'http', kind: 'http', target: 'https://shop.example/health', derived: true, expectedStatus: 200,
  hasBodyMarker: false, intervalSeconds: 60, timeoutSeconds: 10, failureThreshold: 3, recoveryThreshold: 2,
  status: 'up', lastSample: { at: '2026-10-19T08:00:00Z', ok: true, latencyMs: 120, status: 200, error: null, certificateNotAfter: null },
  consecutiveFailures: 0, uptime24h: 0.9995, uptime7d: 0.999, uptime30d: 0.99, latency24h: { p50Ms: 110, p95Ms: 240, p99Ms: 300 },
  certificateNotAfter: null, ...overrides,
});

export const healthResponse: EnvironmentHealthResponse = {
  capturedAt: '2026-10-19T08:00:00Z',
  probeOrigin: 'control-plane',
  environments: [
    {
      project: 'Demo', environmentId: 'production', name: 'Production', kind: 'production', address: 'https://shop.example',
      status: 'down', uptime24h: 0.9861, uptime7d: 0.998, uptime30d: 0.9995, latency24h: { p50Ms: 110, p95Ms: 240, p99Ms: 300 },
      currentIncident: { id: 'production/http/20261019T074500Z', environmentId: 'production', checkId: 'http',
        startedAt: '2026-10-19T07:45:00Z', firstError: 'timeout', failedProbes: 15, endedAt: null, cardId: 'health-production-http-202610190745' },
      lastIncident: null,
      checks: [
        check({ status: 'down', consecutiveFailures: 15,
          lastSample: { at: '2026-10-19T08:00:00Z', ok: false, latencyMs: null, status: null, error: 'timeout', certificateNotAfter: null } }),
        check({ id: 'tls', kind: 'tls', target: 'shop.example:443', certificateNotAfter: '2026-10-29T08:00:00Z',
          lastSample: { at: '2026-10-19T08:00:00Z', ok: true, latencyMs: 40, status: null, error: null, certificateNotAfter: '2026-10-29T08:00:00Z' } }),
      ],
    },
  ],
};
