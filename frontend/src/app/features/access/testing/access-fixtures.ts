import { computed, signal } from '@angular/core';
import { vi } from 'vitest';
import type { AccessOverview, AccessProject, EnrolledRunner, IamRole, RunnerEnrollment } from '../models/access.model';

/** One-time values are assembled at runtime so no credential-shaped literal lands in source. */
export const ENROLLMENT_CODE = ['enr', 'code', 'value'].join('.');
export const SERVICE_ACCOUNT_TOKEN = ['sat', 'tok_1', 'secret-value'].join('.');

export const ACCESS_ROLES: IamRole[] = [
  { id: 'owner', displayName: 'Owner', description: 'Break-glass role.', permissions: ['access.manage'], bindableScopes: ['workspace'], serviceAccountBindable: false },
  { id: 'admin', displayName: 'Admin', description: 'Runs a scope.', permissions: ['project.settings.write'], bindableScopes: ['workspace', 'project'], serviceAccountBindable: true },
  { id: 'developer', displayName: 'Developer', description: 'Works on cards.', permissions: ['project.cards.write'], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: true },
  { id: 'contributor', displayName: 'Product', description: 'Product role.', permissions: ['project.cards.write'], bindableScopes: ['project'], serviceAccountBindable: false },
  { id: 'viewer', displayName: 'Viewer', description: 'Reads.', permissions: ['project.read'], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: true },
  { id: 'approver', displayName: 'Approver', description: 'Approves production.', permissions: ['environment.production.deploy.approve'], bindableScopes: ['workspace', 'project', 'environment'], serviceAccountBindable: false },
];

export const ACCESS_PROJECTS: AccessProject[] = [
  { id: 'PROJ-001', displayName: 'Website' },
  { id: 'PROJ-002', displayName: 'App' },
];

export function accessOverview(overrides: Partial<AccessOverview> = {}): AccessOverview {
  return {
    roles: ACCESS_ROLES,
    permissions: [],
    users: [
      { id: 'usr_owner', username: 'first.owner', displayName: 'First Owner', role: 'owner', projects: [], disabled: false, mustChangePassword: false },
      { id: 'usr_dev', username: 'dev.user', displayName: 'Dev User', role: 'operator', projects: ['PROJ-001'], disabled: false, mustChangePassword: true },
    ],
    groups: [{ id: 'grp_web', name: 'Team Website', description: 'Website crew', members: ['usr_dev'], createdAt: '2026-10-20T09:00:00Z' }],
    serviceAccounts: [],
    bindings: [
      { id: 'rb_owner', principalKind: 'user', principalId: 'usr_owner', role: 'owner', scope: { kind: 'workspace' }, source: 'bootstrap', createdAt: '2026-10-20T09:00:00Z' },
      { id: 'rb_dev', principalKind: 'user', principalId: 'usr_dev', role: 'developer', scope: { kind: 'project', project: 'PROJ-001' }, source: 'manual', createdAt: '2026-10-20T09:00:00Z' },
      { id: 'rb_group', principalKind: 'group', principalId: 'grp_web', role: 'approver', scope: { kind: 'environment', project: 'PROJ-001', environment: 'production' }, source: 'manual', createdAt: '2026-10-20T09:00:00Z' },
    ],
    invitations: [{ id: 'inv_1', userId: 'usr_dev', expiresAt: '2026-10-21T09:00:00Z', used: false }],
    iamVersion: 1,
    ...overrides,
  };
}

/** A signal-backed stand-in for AccessService with spy mutations. */
export function fakeAccessService(overview: AccessOverview | null = accessOverview()) {
  const state = signal<'idle' | 'loading' | 'ready' | 'denied' | 'error'>('ready');
  const overviewSignal = signal(overview);
  return {
    overview: overviewSignal,
    projects: signal(ACCESS_PROJECTS),
    enrollments: signal<readonly RunnerEnrollment[]>([]),
    runners: signal<readonly EnrolledRunner[]>([]),
    state,
    busy: signal(false),
    error: signal<string | null>(null),
    projectsError: signal<string | null>(null),
    runnersError: signal<string | null>(null),
    pendingInvitations: computed(() => new Set((overviewSignal()?.invitations ?? []).filter(item => !item.used).map(item => item.userId))),
    load: vi.fn(() => Promise.resolve()),
    invite: vi.fn(() => Promise.resolve({ user: { username: 'new.user' }, signInLink: '/?invite=inv.abc', expiresAt: '2026-10-21T09:00:00Z' })),
    addBinding: vi.fn(() => Promise.resolve({})),
    removeBinding: vi.fn(() => Promise.resolve({})),
    createGroup: vi.fn(() => Promise.resolve({})),
    updateGroupMembers: vi.fn(() => Promise.resolve({})),
    deleteGroup: vi.fn(() => Promise.resolve({})),
    createServiceAccount: vi.fn(() => Promise.resolve({})),
    createToken: vi.fn(() => Promise.resolve({ serviceAccountId: 'sa_ci', tokenId: 'tok_1', token: SERVICE_ACCOUNT_TOKEN, expiresAt: '2027-01-18T09:00:00Z' })),
    revokeToken: vi.fn(() => Promise.resolve({})),
    disableServiceAccount: vi.fn(() => Promise.resolve({})),
    resetPassword: vi.fn(() => Promise.resolve({ temporaryPassword: ['Tmp', 'one', 'time'].join('-') })),
    setUserDisabled: vi.fn(() => Promise.resolve({})),
    createEnrollment: vi.fn(() => Promise.resolve({ enrollmentCode: ENROLLMENT_CODE })),
    revokeEnrollment: vi.fn(() => Promise.resolve({})),
    revokeRunner: vi.fn(() => Promise.resolve({})),
  };
}
