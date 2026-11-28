import {
  ALL_RUNNER_PROJECTS, RUN_HOST_SCOPE, formatAccessTime, repositoryAccess, scopeLabel,
  type AccessOverview, type AccessProject, type EnrolledRunner, type IamPrincipalKind, type RoleBinding, type RunnerEnrollment,
  type RepositoryAccess, type RunnerCredential, type ServiceAccount, type ServiceAccountToken,
} from './access.model';
import type { GridColumn } from '../../../components/data-grid';

/**
 * Presentation model of the Access page: the one column logic every grid
 * follows, and a role binding as it is shown (role, scope, origin).
 */

/** Shared widths, so the Access columns line up from section to section. */
const WIDTH = { identity: '22%', state: '10rem', meta: '13.5rem', actions: '3rem' } as const;

/** Who or what (rows carry `name` and `sub`), what it may reach, its state, its credential fact, its actions. */
export function accessColumns(identity: string, summary: string, meta: string, state: string | null = 'State'): GridColumn[] {
  return [
    { key: 'name', subKey: 'sub', header: identity, role: 'identity', width: WIDTH.identity },
    { key: 'access', header: summary, role: 'summary' },
    ...(state ? [{ key: 'state', header: state, role: 'state', width: WIDTH.state } as GridColumn] : []),
    { key: 'meta', header: meta, role: 'meta', width: WIDTH.meta },
    { key: 'actions', header: 'Actions', role: 'actions', width: WIDTH.actions },
  ];
}

/** One role binding as a chip: the role, where it applies, and the group it comes through. */
export interface GrantView {
  id: string;
  role: string;
  scope: string;
  /** Name of the group a person holds this role through; null for a direct binding. */
  via: string | null;
  /** The break-glass role, shown with emphasis. */
  owner: boolean;
  binding: RoleBinding;
}

export function roleName(overview: AccessOverview | null, id: string): string {
  return overview?.roles.find(role => role.id === id)?.displayName ?? id;
}

/** Each scope the principal holds a role in; a person also holds the roles of their groups. */
export function grantsOf(kind: IamPrincipalKind, id: string, overview: AccessOverview | null,
  projects: readonly AccessProject[]): GrantView[] {
  if (!overview) return [];
  const groups = kind === 'user' ? overview.groups.filter(group => group.members.includes(id)) : [];
  const view = (binding: RoleBinding, via: string | null): GrantView => ({
    id: binding.id, role: roleName(overview, binding.role), scope: scopeLabel(binding.scope, projects), via,
    owner: binding.role === 'owner', binding,
  });
  return overview.bindings.flatMap(binding => {
    if (binding.principalKind === kind && binding.principalId === id) return [view(binding, null)];
    const group = binding.principalKind === 'group' ? groups.find(candidate => candidate.id === binding.principalId) : undefined;
    return group ? [view(binding, group.name)] : [];
  });
}

export function isApiToken(account: ServiceAccount): boolean {
  return account.origin === 'api-token';
}

export function activeTokens(account: ServiceAccount): ServiceAccountToken[] {
  return account.tokens.filter(token => !token.revokedAt);
}

/** A UTC date without the time, for a grid cell; the detail shows the full timestamp. */
export function formatAccessDate(value: string | number): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleDateString('en-GB', { dateStyle: 'medium', timeZone: 'UTC' });
}

/** "2 tokens, next expires 1 Dec 2026": the credential fact of a service account. */
export function tokenSummary(account: ServiceAccount, now: number): string {
  const tokens = activeTokens(account);
  if (tokens.length === 0) return 'No token';
  const ends = tokens.map(token => Date.parse(token.expiresAt ?? '')).filter(value => !Number.isNaN(value));
  const count = `${tokens.length} ${tokens.length === 1 ? 'token' : 'tokens'}`;
  if (ends.length === 0) return `${count}, no expiry`;
  const soonest = Math.min(...ends);
  const when = formatAccessDate(soonest);
  return soonest <= now ? `${count}, expired ${when}` : `${count}, ${tokens.length === 1 ? 'expires' : 'next expires'} ${when}`;
}

export type RunnerIdentity = 'runner' | 'deploy' | 'orchestrator';

const IDENTITY_LABEL: Record<RunnerIdentity, string> = {
  runner: 'Coding and review',
  deploy: 'Run host',
  orchestrator: 'Orchestration engine',
};

export function identityLabel(identity: RunnerIdentity): string {
  return IDENTITY_LABEL[identity];
}

/** The engine authenticates with an orchestration credential; a run host holds only runner.deploy (F4). */
export function runnerIdentity(runner: EnrolledRunner): RunnerIdentity {
  if (runner.id.startsWith('engine_')) return 'orchestrator';
  return runner.credentials.some(credential => credential.scopes.includes(RUN_HOST_SCOPE)) ? 'deploy' : 'runner';
}

export function enrollmentIdentity(enrollment: RunnerEnrollment): RunnerIdentity {
  if (enrollment.scopes.includes('orchestration.write')) return 'orchestrator';
  return enrollment.scopes.includes(RUN_HOST_SCOPE) ? 'deploy' : 'runner';
}

export function runnerCredentialActive(credential: RunnerCredential, now: number): boolean {
  return !credential.revokedAt && (!credential.expiresAt || Date.parse(credential.expiresAt) > now);
}

/** During rotation, every valid credential can still grant access. */
export function runnerRepositoryAccess(runner: EnrolledRunner, now: number): RepositoryAccess {
  if (runner.revokedAt) return 'none';
  return repositoryAccess(runner.credentials
    .filter(credential => runnerCredentialActive(credential, now))
    .flatMap(credential => credential.scopes));
}

export function projectNames(projects: readonly string[] | null | undefined, known: readonly AccessProject[]): string {
  const list = projects ?? [];
  if (list.includes(ALL_RUNNER_PROJECTS)) return 'All projects';
  if (list.length === 0) return 'No project';
  const names = new Map(known.map(project => [project.id.toLowerCase(), project.displayName]));
  return list.map(project => names.get(project.toLowerCase()) ?? project).join(', ');
}

export type PersonStatus = 'Disabled' | 'Invited' | 'Active' | 'No access';

/** Disabled wins over a pending invitation; a person without any role has no access. */
export function personStatus(disabled: boolean, invited: boolean, grants: number): PersonStatus {
  if (disabled) return 'Disabled';
  if (invited) return 'Invited';
  return grants ? 'Active' : 'No access';
}

/** Account state on the shared indicator: Active succeeds, Invited waits, the rest is settled. */
export function personTone(status: PersonStatus): 'success' | 'info' | 'neutral' {
  return status === 'Active' ? 'success' : status === 'Invited' ? 'info' : 'neutral';
}

/** A credential within this many days of its end is shown as expiring. */
const EXPIRY_WARNING_DAYS = 14;
const DAY_MS = 24 * 60 * 60 * 1000;

export interface RunnerExpiry {
  label: string;
  /** Null renders the date as plain text: a distant expiry is a fact, not a state. */
  tone: 'neutral' | 'danger' | 'warning' | null;
  notice: string | null;
}

/** The soonest end among the runner's active credentials, and why one has none. */
export function runnerExpiry(runner: EnrolledRunner, now: number): RunnerExpiry {
  const active = runner.credentials.filter(credential => !credential.revokedAt);
  const unexpiring = active.find(credential => !credential.expiresAt);
  if (unexpiring) return { label: 'No expiry', tone: 'neutral', notice: unexpiring.expiryNotice ?? null };
  const ends = active.map(credential => Date.parse(credential.expiresAt!)).filter(Number.isFinite);
  if (ends.length === 0) return { label: '', tone: null, notice: null };
  const upcoming = ends.filter(end => end > now);
  const soonest = Math.min(...(upcoming.length ? upcoming : ends));
  const days = Math.ceil((soonest - now) / DAY_MS);
  if (days <= 0) return { label: 'Expired', tone: 'danger', notice: null };
  if (days <= EXPIRY_WARNING_DAYS)
    return { label: days === 1 ? 'Expires in 1 day' : `Expires in ${days} days`, tone: 'warning', notice: null };
  return { label: `Expires ${formatAccessTime(new Date(soonest).toISOString())}`, tone: null, notice: null };
}

export type EnrollmentStatus = 'Used' | 'Open' | 'Expired or revoked';

export function enrollmentOpen(enrollment: RunnerEnrollment, now: number): boolean {
  return !enrollment.usedAt && Date.parse(enrollment.expiresAt) > now;
}

export function enrollmentStatus(enrollment: RunnerEnrollment, now: number): EnrollmentStatus {
  if (enrollment.usedAt) return 'Used';
  return enrollmentOpen(enrollment, now) ? 'Open' : 'Expired or revoked';
}

/** Enrollment state on the shared indicator: an open code waits, a used one succeeded. */
export function enrollmentTone(status: EnrollmentStatus): 'success' | 'info' | 'neutral' {
  return status === 'Used' ? 'success' : status === 'Open' ? 'info' : 'neutral';
}

/** Choosing every project replaces single choices, and a single choice leaves "every project". */
export function toggleProject(current: readonly string[], project: string): readonly string[] {
  if (current.includes(project)) return current.filter(item => item !== project);
  if (project === ALL_RUNNER_PROJECTS) return [ALL_RUNNER_PROJECTS];
  return [...current.filter(item => item !== ALL_RUNNER_PROJECTS), project];
}
