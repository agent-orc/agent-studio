/**
 * Wire types of the operator operation catalogue and standing grants (SM-4):
 * every operator request is T0 observe, T1 routine under a standing grant, or
 * T2 high risk for the Owner's approval (`/api/auth/iam/operator-catalogue`,
 * `/api/auth/iam/standing-grants`).
 */

export type OperatorTier = 'T0' | 'T1' | 'T2';
export type GrantScopeKind = 'workspace' | 'project' | 'environment' | 'host';
export type StandingGrantState = 'active' | 'expired' | 'revoked';

export interface OperatorOperation {
  id: string;
  tier: OperatorTier;
  scopeKind: GrantScopeKind;
  title: string;
}

export interface OperatorCatalogue {
  operations: readonly OperatorOperation[];
  defaultGrantDays: number;
  maxGrantDays: number;
  defaultRatePerHour: number;
  maxRatePerHour: number;
}

export interface StandingGrantScope {
  kind: GrantScopeKind;
  project?: string | null;
  environment?: string | null;
  host?: string | null;
}

/** A daily UTC window, `HH:mm` each; a start after the end wraps past midnight. */
export interface StandingGrantWindow {
  startUtc: string;
  endUtc: string;
}

export interface StandingGrant {
  id: string;
  principalId: string;
  principalName: string | null;
  tier: OperatorTier;
  operations: readonly string[];
  scope: StandingGrantScope;
  ratePerHour: number;
  window: StandingGrantWindow | null;
  issuedBy: string;
  issuedAt: string;
  expiresAt: string;
  renewedAt: string | null;
  renewedBy: string | null;
  revokedAt: string | null;
  revokedBy: string | null;
  reason: string;
  revokeReason: string | null;
  state: StandingGrantState;
  usesLastHour: number;
}

export interface CreateStandingGrant {
  principalId: string;
  operations: string[];
  scope: StandingGrantScope;
  ratePerHour: number;
  window: StandingGrantWindow | null;
  expiresInDays: number;
  reason: string;
}

export const GRANT_SCOPE_KINDS: readonly GrantScopeKind[] = ['workspace', 'project', 'environment', 'host'];
export const GRANT_ENVIRONMENTS: readonly string[] = ['stage', 'production'];

/** The scope kinds a grant of `grantKind` can cover, as the server checks them. */
export function scopeKindCovers(grantKind: GrantScopeKind, operationKind: GrantScopeKind): boolean {
  switch (grantKind) {
    case 'workspace': return true;
    case 'project': return operationKind === 'project' || operationKind === 'environment';
    case 'environment': return operationKind === 'environment';
    case 'host': return operationKind === 'host';
  }
}

export function tierLabel(tier: OperatorTier): string {
  return tier === 'T0' ? 'T0 observe' : tier === 'T1' ? 'T1 routine' : 'T2 high risk';
}

export function grantStateLabel(state: StandingGrantState): string {
  return state === 'active' ? 'Active' : state === 'expired' ? 'Expired' : 'Revoked';
}

export function scopeLabel(scope: StandingGrantScope): string {
  switch (scope.kind) {
    case 'workspace': return 'Workspace';
    case 'project': return `Project ${scope.project ?? ''}`.trim();
    case 'environment': return `${scope.project ?? ''} ${scope.environment ?? ''}`.trim();
    case 'host': return `Host ${scope.host ?? ''}`.trim();
  }
}

export function windowLabel(window: StandingGrantWindow | null): string {
  return window ? `${window.startUtc} to ${window.endUtc} UTC` : 'Any time';
}

/** Live grants first, then newest first. */
export function orderedGrants(grants: readonly StandingGrant[]): StandingGrant[] {
  return [...grants].sort((left, right) =>
    Number(right.state === 'active') - Number(left.state === 'active')
    || right.issuedAt.localeCompare(left.issuedAt));
}

/** True for a valid `HH:mm` value. */
export function isTimeOfDay(value: string): boolean {
  return /^([01]\d|2[0-3]):[0-5]\d$/.test(value);
}
