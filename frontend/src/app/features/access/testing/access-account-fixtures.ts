import type { ServiceAccount } from '../models/access.model';

const created = '2026-10-20T09:00:00Z';
const token = (id: string, expiresAt: string, revokedAt: string | null = null) =>
  ({ id, name: `${id} token`, createdAt: created, expiresAt, lastUsedAt: null, revokedAt });
export const ACCOUNTS: ServiceAccount[] = [
  { id: 'sa_ci', name: 'ci-bot', description: 'Deploys stage', createdAt: created, tokens: [
    token('tok_1', '2027-01-18T09:00:00Z'), token('tok_2', '2026-12-01T09:00:00Z'), token('tok_gone', '2026-11-01T09:00:00Z', created),
  ] },
  { id: 'sa_old', name: 'old-bot', description: null, createdAt: created, disabledAt: created, tokens: [] },
  { id: 'sa_tok_build', name: 'build bot', description: 'Migrated API token.', createdAt: created, origin: 'api-token', createdBy: 'usr_owner',
    tokens: [token('tok_build', '2026-11-19T09:00:00Z')] },
  { id: 'sa_operator', name: 'release-operator', description: null, createdAt: created, origin: 'operator-agent', tokens: [] },
];
export const ACCOUNT_BINDINGS = [
  { id: 'rb_sa', principalKind: 'service-account' as const, principalId: 'sa_ci', role: 'developer',
    scope: { kind: 'environment' as const, project: 'PROJ-001', environment: 'stage' }, source: 'manual', createdAt: created },
  { id: 'rb_build', principalKind: 'service-account' as const, principalId: 'sa_tok_build', role: 'operator-agent',
    scope: { kind: 'workspace' as const }, source: 'migration', createdAt: created },
];

