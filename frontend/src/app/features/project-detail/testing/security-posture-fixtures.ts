import type { SecurityFinding, SecurityPosture } from '../components/operations-security/operations-security.model';

/** Shared GET /api/security/posture answer for the unit spec and the mocked Playwright spec (AGT-3575). */
export const SECURITY_POSTURE_FIXTURE_NOW = '2026-11-09T12:00:00Z';

export function securityFinding(rank: number, id: string, score: number, severity: SecurityFinding['severity'], actionSoon: boolean,
  area: SecurityFinding['area'], title: string, detail: string, dueAt: string | null = null): SecurityFinding {
  return { id, rank, score, severity, area, title, detail, actionSoon, dueAt, subject: null };
}

export function securityPostureFixture(): SecurityPosture {
  return {
    generatedAt: SECURITY_POSTURE_FIXTURE_NOW,
    summary: { total: 6, critical: 1, high: 2, medium: 2, low: 1, info: 0, actionSoon: 3 },
    findings: [
      securityFinding(1, 'second-factor:u1', 95, 'critical', true, 'second-factor', 'Owner has no second factor',
        'This role must use TOTP or a passkey; until one is enrolled the account can only open the enrollment step.'),
      securityFinding(2, 'credentials:token:t1:expiring', 74, 'high', true, 'credentials', 'API token laptop expires in 10 days',
        'Renew or replace it before it stops working.', '2026-11-19T12:00:00Z'),
      securityFinding(3, 'versions:cli:r1:claude', 74, 'high', true, 'versions', 'claude 2.1.200 on runner-1 is below the required 2.1.305',
        'Update the CLI on this host; older CLIs miss provider fixes and model support.'),
      securityFinding(4, 'credentials:token:t2:no-expiry', 50, 'medium', false, 'credentials', 'Service account ci token build never expires',
        'A token without an expiry stays valid until someone revokes it. Replace it with one that expires.'),
      securityFinding(5, 'audit:refused-request:api-token:t1', 45, 'medium', false, 'audit', '3 refused requests by api-token:t1',
        'In the last 7 days. Latest: GET /api/auth/iam answered 403.'),
      securityFinding(6, 'cards:SEC-1', 35, 'low', false, 'cards', 'Open security card SEC-1: Rotate edge certificate', 'In Studio.'),
    ],
    exposure: {
      profile: 'networked', httpsRequired: true, cloudflareEdge: true, trustedProxyCount: 2, loginAddressHealthy: true,
      anonymousRoutes: ['GET /healthz', 'POST /api/auth/login'],
      external: [{ id: 'firewall', label: 'Host firewall', state: 'not-reported', detail: 'Applied by host scripts.' }],
    },
    credentials: {
      tokens: [
        { id: 't1', holder: 'laptop', kind: 'api-token', role: 'operator', projects: ['*'], createdAt: '2026-10-01T00:00:00Z',
          expiresAt: '2026-11-19T12:00:00Z', lastUsedAt: '2026-11-09T10:00:00Z', state: 'expiring' },
        { id: 't2', holder: 'ci', kind: 'service-account', role: 'Developer', projects: ['mine'], createdAt: '2026-10-01T00:00:00Z',
          expiresAt: null, lastUsedAt: null, state: 'no-expiry' },
      ],
      operatorAgents: [{ id: 'opa_1', name: 'laptop agent', disabled: false, liveSessions: 1, nextSessionExpiry: '2026-11-09T18:00:00Z' }],
      inventory: [{ id: 'provider-login:r1:claude', category: 'provider', kind: 'claude_login', label: 'claude login on runner-1',
        hostId: 'host-1', expiresAt: '2026-11-15T12:00:00Z', lastUseAt: null, state: 'expiring', detail: 'The host reports a working login.' }],
      inventorySource: 'task-server', inventoryProblem: null,
    },
    secondFactor: {
      activeUsers: 2, covered: 1,
      users: [
        { id: 'u1', username: 'owner', displayName: 'Owner', role: 'owner', disabled: false, totp: false, passkeys: 0,
          recoveryCodes: 0, required: true, state: 'missing' },
        { id: 'u2', username: 'dev', displayName: 'Dev', role: 'contributor', disabled: false, totp: true, passkeys: 1,
          recoveryCodes: 8, required: false, state: 'covered' },
      ],
    },
    versions: {
      server: { version: '1.4.0', tag: 'v1.4.0', commit: 'abcdef1234567890', builtAt: '2026-11-01T00:00:00Z', known: true },
      hostSource: 'local', hostProblem: null,
      hosts: [{ runnerId: 'r1', name: 'runner-1', hostId: 'host-1', status: 'active', runnerVersion: 'runner 1.3.0',
        releaseVersion: '1.3.0', releaseState: 'behind', operatingSystem: 'Ubuntu 24.04.1 LTS', lastSeenAt: '2026-11-09T11:59:00Z',
        clis: [{ name: 'claude', version: '2.1.200', targetVersion: '2.1.305', belowTarget: true }], containers: [] }],
    },
    audit: {
      integrityState: 'verified', integrityCheckedAt: '2026-11-09T11:45:00Z', sinkConfigured: true, pendingForward: 0,
      sinkAlert: false, openAlerts: [], lastKillAt: null, lastDrillAt: '2026-11-01T03:00:00Z', lastDrillPassed: true,
      activeBreakGlassWindows: 0, windowDays: 7,
      anomalies: [{ at: '2026-11-09T09:00:00Z', kind: 'refused-request', stream: 'api-token', actor: 'api-token:t1',
        operation: 'GET /api/auth/iam', outcome: '403', count: 3 }],
    },
    cards: [{ key: 'SEC-1', title: 'Rotate edge certificate', project: 'Studio', state: '2-ready', createdAt: '2026-11-01T00:00:00Z' }],
  };
}
