/** Wire types of the Access area (`/api/auth/iam`, IP-8 rights model). */

export type IamScopeKind = 'workspace' | 'project' | 'environment';
export type IamPrincipalKind = 'user' | 'group' | 'service-account';
export type PermissionLevel = 'workspace' | 'project' | 'environment';

export interface IamScope {
  kind: IamScopeKind;
  project?: string | null;
  environment?: string | null;
}

export interface IamRole {
  id: string;
  displayName: string;
  description: string;
  permissions: readonly string[];
  bindableScopes: readonly IamScopeKind[];
  serviceAccountBindable: boolean;
}

export interface IamPermission {
  name: string;
  level: PermissionLevel;
  description: string;
}

export interface AccessUser {
  id: string;
  username: string;
  displayName: string;
  role: string;
  projects: readonly string[];
  disabled: boolean;
  mustChangePassword: boolean;
}

export interface AccessGroup {
  id: string;
  name: string;
  description?: string | null;
  members: readonly string[];
  createdAt: string;
}

export interface ServiceAccountToken {
  id: string;
  name?: string | null;
  createdAt: string;
  expiresAt?: string | null;
  lastUsedAt?: string | null;
  revokedAt?: string | null;
}

export interface ServiceAccount {
  id: string;
  name: string;
  description?: string | null;
  createdAt: string;
  disabledAt?: string | null;
  tokens: readonly ServiceAccountToken[];
}

export interface RoleBinding {
  id: string;
  principalKind: IamPrincipalKind;
  principalId: string;
  role: string;
  scope: IamScope;
  source: string;
  createdAt: string;
  createdBy?: string | null;
}

export interface AccessInvitation {
  id: string;
  userId: string;
  expiresAt: string;
  used: boolean;
}

export interface AccessOverview {
  roles: readonly IamRole[];
  permissions: readonly IamPermission[];
  users: readonly AccessUser[];
  groups: readonly AccessGroup[];
  serviceAccounts: readonly ServiceAccount[];
  bindings: readonly RoleBinding[];
  invitations: readonly AccessInvitation[];
  iamVersion: number;
}

export interface IamGrant {
  role: string;
  scope: IamScope;
}

export interface AccessProject {
  id: string;
  displayName: string;
}

export interface InvitationResult {
  user: AccessUser;
  signInLink: string;
  expiresAt: string;
}

export interface OneTimeServiceAccountToken {
  serviceAccountId: string;
  tokenId: string;
  token: string;
  expiresAt?: string | null;
}

/** One scope with the bindings that apply in it, workspace first. */
export interface ScopeBindingGroup {
  key: string;
  scope: IamScope;
  bindings: readonly RoleBinding[];
}

export function scopeKey(scope: IamScope): string {
  if (scope.kind === 'workspace') return 'workspace';
  if (scope.kind === 'project') return `project:${scope.project ?? ''}`.toLowerCase();
  return `environment:${scope.project ?? ''}/${scope.environment ?? ''}`.toLowerCase();
}

export function scopeLabel(scope: IamScope, projects: readonly AccessProject[] = []): string {
  if (scope.kind === 'workspace') return 'Workspace';
  const project = projectName(scope.project ?? '', projects);
  return scope.kind === 'project' ? project : `${project} · ${scope.environment ?? ''}`;
}

export function projectName(handle: string, projects: readonly AccessProject[]): string {
  const match = projects.find(project => project.id.toLowerCase() === handle.toLowerCase()
    || project.displayName.toLowerCase() === handle.toLowerCase());
  return match?.displayName ?? handle;
}

/** Bindings grouped per scope: the workspace, then projects and their environments by name. */
export function groupBindingsByScope(bindings: readonly RoleBinding[], projects: readonly AccessProject[] = []): ScopeBindingGroup[] {
  const groups = new Map<string, { scope: IamScope; bindings: RoleBinding[] }>();
  for (const binding of bindings) {
    const key = scopeKey(binding.scope);
    const group = groups.get(key) ?? { scope: binding.scope, bindings: [] };
    group.bindings.push(binding);
    groups.set(key, group);
  }
  return [...groups.entries()]
    .map(([key, group]) => ({ key, scope: group.scope, bindings: group.bindings }))
    .sort((left, right) => scopeOrder(left.scope, projects).localeCompare(scopeOrder(right.scope, projects)));
}

function scopeOrder(scope: IamScope, projects: readonly AccessProject[]): string {
  if (scope.kind === 'workspace') return '0';
  const project = projectName(scope.project ?? '', projects).toLowerCase();
  return scope.kind === 'project' ? `1:${project}:0` : `1:${project}:1:${scope.environment ?? ''}`;
}

export function principalName(kind: IamPrincipalKind, id: string, overview: AccessOverview | null): string {
  if (!overview) return id;
  if (kind === 'user') return overview.users.find(user => user.id === id)?.displayName ?? id;
  if (kind === 'group') return overview.groups.find(group => group.id === id)?.name ?? id;
  return overview.serviceAccounts.find(account => account.id === id)?.name ?? id;
}

export function principalKindLabel(kind: IamPrincipalKind): string {
  return kind === 'service-account' ? 'Service account' : kind === 'group' ? 'Group' : 'User';
}

/** Roles that may be bound to this principal kind in this scope kind. */
export function bindableRoles(roles: readonly IamRole[], kind: IamPrincipalKind, scope: IamScopeKind): IamRole[] {
  return roles.filter(role => role.bindableScopes.includes(scope)
    && (kind !== 'service-account' || role.serviceAccountBindable));
}

/** A UTC timestamp for access records; locale and zone are fixed so every reader sees the same value. */
export function formatAccessTime(value: string | null | undefined): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return `${date.toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' })} UTC`;
}
