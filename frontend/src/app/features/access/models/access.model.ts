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
  /** False for a role only service accounts take (`operator-agent`); absent from older servers. */
  userBindable?: boolean;
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
  hasFactor?: boolean;
  factorChangedAt?: string | null;
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
  /** 'api-token' for an API token (one `api.` token, bound to its creator). */
  origin?: string | null;
  createdBy?: string | null;
}

/** The project entry that binds a runner to every project. */
export const ALL_RUNNER_PROJECTS = '*';

export type EnrollmentKind = 'coding' | 'review' | 'orchestrator' | 'deploy';
export type RepositoryAccess = 'none' | 'read' | 'write';

export const REPOSITORY_ACCESS_LABELS: Record<RepositoryAccess, string> = {
  none: 'No repository access',
  read: 'Read repositories',
  write: 'Read and push to repositories',
};

const REPOSITORY_READ = 'repository.read';
const REPOSITORY_WRITE = 'repository.write';
const RUNNER_MINIMUM = ['runner.claim', 'runner.lease', 'runner.logs', 'runner.events', 'runner.artifacts', 'runner.completion'];

export function repositoryAccess(scopes: readonly string[]): RepositoryAccess {
  if (scopes.includes(REPOSITORY_WRITE)) return 'write';
  return scopes.includes(REPOSITORY_READ) ? 'read' : 'none';
}

export function withRepositoryAccess(access: RepositoryAccess, scopes: readonly string[] = RUNNER_MINIMUM): string[] {
  const base = scopes.filter(scope => scope !== REPOSITORY_READ && scope !== REPOSITORY_WRITE);
  if (access !== 'none') base.push(access === 'read' ? REPOSITORY_READ : REPOSITORY_WRITE);
  return base;
}

export interface CreatedEnrollment {
  enrollmentCode: string;
  repositoryAccess: RepositoryAccess;
}

export interface IssuedRunnerCredential {
  runnerId: string;
  credentialId: string;
  secret: string;
  scopes: readonly string[];
  expiresAt: string | null;
}

/** The one scope of a run host credential; deploy credentials carry no other scope. */
export const RUN_HOST_SCOPE = 'runner.deploy';

/** Where the run host keeps its enrollment and the env file `agent-runner-deploy.service` reads. */
const RUN_HOST_CONFIG = '/etc/agent-runner/run-host';
const RUN_HOST_ENV = '/etc/agent-runner/deploy.env';

/**
 * Host commands for a run host code (F4). Enrollment prints and saves the
 * runner id (`runner_...`); the deploy service claims with that id as its host
 * id, so the second command copies it into the service environment. The same
 * id is the `targetHostId` of every environment this host serves.
 */
export function runHostCommand(serverUrl: string, code: string): string {
  const url = serverUrl.replace(/\/+$/, '');
  return [
    `sudo RUNNER_ENROLLMENT_CONFIG_DIR=${RUN_HOST_CONFIG} /opt/agent-host/agent-host --enroll ${url} ${code}`,
    `sudo sh -c 'mkdir -p /var/lib/agent-host/artifacts && . ${RUN_HOST_CONFIG}/installation.env && printf "RUNNER_SERVER_URL=%s\\nRUNNER_HOSTNAME=%s\\n`
      + `RUNNER_AUTH_TOKEN_FILE=%s\\nDEPLOY_ARTIFACT_STORE=/var/lib/agent-host/artifacts\\n`
      + `DEPLOY_SECRET_STORE=/etc/agent-host/secrets\\n" "$RUNNER_SERVER_URL" "$RUNNER_ID" "$RUNNER_AUTH_TOKEN_FILE"`
      + ` > ${RUN_HOST_ENV}'`,
    'sudo systemctl enable --now agent-runner-deploy.service',
  ].join('\n');
}

export interface RunnerEnrollment {
  id: string;
  name: string;
  scopes: readonly string[];
  createdAt: string;
  expiresAt: string;
  usedAt?: string | null;
  /** Projects the enrolled runner is bound to (SM-9); absent on a code from before project binding. */
  projects?: readonly string[] | null;
}

export interface RunnerCredential {
  id: string;
  scopes: readonly string[];
  createdAt?: string;
  expiresAt?: string | null;
  lastUsedAt?: string | null;
  revokedAt?: string | null;
  /** Set for a credential issued before the 30-day default: it keeps working without expiry. */
  expiryNotice?: string | null;
}

export interface EnrolledRunner {
  id: string;
  name: string;
  createdAt: string;
  revokedAt?: string | null;
  lastSeenAt?: string | null;
  /** Bound projects; `*` binds every project and an empty list binds none (SM-9). */
  projects?: readonly string[] | null;
  /** Set for a runner enrolled before project binding: it holds every project until the Owner chooses. */
  projectsNotice?: string | null;
  credentials: readonly RunnerCredential[];
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
    && (kind === 'service-account' ? role.serviceAccountBindable : role.userBindable !== false));
}

/** A UTC timestamp for access records; locale and zone are fixed so every reader sees the same value. */
export function formatAccessTime(value: string | null | undefined): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return `${date.toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' })} UTC`;
}
